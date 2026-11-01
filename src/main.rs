// Forge bootstrap compiler (Rust). Lexer structure recovered from the transcript;
// parser/codegen rewritten (transcript copies were truncated). Single-pass:
// tokens -> x86-64 GNU as text. Mirrors the self-hosted compiler's design.
use std::collections::HashMap;
use std::env;
use std::fs;
use std::process::{exit, Command};

#[derive(Debug, Clone, PartialEq)]
enum T { Num(i64), Str(String), Id(String), Op(String), Nl, Indent, Dedent, Eof }

fn die(line: usize, msg: &str) -> ! { eprintln!("error: line {}: {}", line, msg); exit(1) }

fn lex(src: &str) -> Vec<(T, usize)> {
    let s: Vec<u8> = src.bytes().collect();
    let mut out = vec![]; let mut i = 0; let mut line = 1; let mut ind = vec![0usize]; let mut bol = true; let mut depth = 0i32;
    while i < s.len() {
        if bol {
            let mut sp = 0; while i < s.len() && s[i] == b' ' { sp += 1; i += 1; }
            if i >= s.len() { break; }
            if s[i] == b'\n' || s[i] == b'#' { while i < s.len() && s[i] != b'\n' { i += 1; } i += 1; line += 1; continue; }
            bol = false;
            if sp > *ind.last().unwrap() { ind.push(sp); out.push((T::Indent, line)); }
            while sp < *ind.last().unwrap() { ind.pop(); out.push((T::Dedent, line)); }
            if sp != *ind.last().unwrap() { die(line, "inconsistent indentation"); }
        }
        let c = s[i];
        if c == b' ' || c == b'\r' || c == b'\t' { i += 1; continue; }
        if c == b'#' { while i < s.len() && s[i] != b'\n' { i += 1; } continue; }
        if c == b'\n' { i += 1; if depth == 0 { out.push((T::Nl, line)); bol = true; } line += 1; continue; }
        if c.is_ascii_digit() { let mut v = 0i64; while i < s.len() && s[i].is_ascii_digit() { v = v * 10 + (s[i] - b'0') as i64; i += 1; } out.push((T::Num(v), line)); continue; }
        if c.is_ascii_alphabetic() || c == b'_' { let st = i; while i < s.len() && (s[i].is_ascii_alphanumeric() || s[i] == b'_') { i += 1; } out.push((T::Id(String::from_utf8_lossy(&s[st..i]).into()), line)); continue; }
        if c == b'"' || c == b'\'' {
            let q = c; i += 1; let mut v = vec![];
            while i < s.len() && s[i] != q {
                if s[i] == b'\n' { die(line, "unterminated string"); }
                if s[i] == b'\\' { i += 1; v.push(match s[i] { b'n' => 10, b't' => 9, b'0' => 0, x => x }); } else { v.push(s[i]); }
                i += 1;
            }
            if i >= s.len() { die(line, "unterminated string"); }
            i += 1;
            if q == b'\'' { if v.len() != 1 { die(line, "char literal must be one character"); } out.push((T::Num(v[0] as i64), line)); }
            else { out.push((T::Str(String::from_utf8_lossy(&v).into()), line)); }
            continue;
        }
        let two = if i + 1 < s.len() { String::from_utf8_lossy(&s[i..i + 2]).to_string() } else { String::new() };
        if ["==", "!=", "<=", ">=", "+=", "-=", "->"].contains(&two.as_str()) { out.push((T::Op(two), line)); i += 2; continue; }
        if b"+-*/%()[]:,=<>.".contains(&c) {
            if c == b'(' || c == b'[' { depth += 1; } if c == b')' || c == b']' { depth -= 1; }
            out.push((T::Op((c as char).to_string()), line)); i += 1; continue; }
        die(line, &format!("unexpected character '{}'", c as char));
    }
    if !bol { out.push((T::Nl, line)); }
    while ind.len() > 1 { ind.pop(); out.push((T::Dedent, line)); }
    out.push((T::Eof, line));
    out
}

struct C {
    t: Vec<(T, usize)>, p: usize, out: Vec<String>, data: Vec<String>, nl: usize, ns: usize,
    globals: Vec<String>, infunc: bool, params: Vec<String>, locals: Vec<String>,
    funcs: HashMap<String, usize>, calls: Vec<(String, usize, usize)>, loops: Vec<(usize, usize)>,
    fields: HashMap<String, usize>, classes: HashMap<String, usize>,
}

impl C {
    fn tok(&self) -> &T { &self.t[self.p].0 }
    fn line(&self) -> usize { self.t[self.p].1 }
    fn is_op(&self, o: &str) -> bool { matches!(self.tok(), T::Op(x) if x == o) }
    fn is_id(&self, o: &str) -> bool { matches!(self.tok(), T::Id(x) if x == o) }
    fn expect(&mut self, o: &str) { if !self.is_op(o) { die(self.line(), &format!("expected '{}'", o)); } self.p += 1; }
    fn ident(&mut self) -> String { if let T::Id(x) = self.tok().clone() { self.p += 1; x } else { die(self.line(), "expected identifier") } }
    fn e(&mut self, s: &str) { self.out.push(format!("    {}\n", s)); }
    fn label(&mut self) -> usize { self.nl += 1; self.nl }
    fn skip_type(&mut self) { // optional annotation: ': type' (type = name or name[name])
        self.ident(); if self.is_op("[") { self.p += 1; self.skip_type(); self.expect("]"); }
    }
    fn var_ref(&self, n: &str) -> Option<String> {
        if self.infunc {
            if let Some(i) = self.params.iter().position(|x| x == n) { return Some(format!("{}(%rbp)", 16 + 8 * (self.params.len() - 1 - i))); }
            if let Some(i) = self.locals.iter().position(|x| x == n) { return Some(format!("-{}(%rbp)", 8 * (i + 1))); }
        }
        if self.globals.iter().any(|x| x == n) { return Some(format!("g_{}(%rip)", n)); }
        None
    }
    fn assign_ref(&mut self, n: &str) -> String {
        if let Some(r) = self.var_ref(n) { return r; }
        if !self.infunc { die(self.line(), "internal: global not prescanned"); }
        self.locals.push(n.to_string()); self.var_ref(n).unwrap()
    }
    // ---------- expressions ----------
    fn expr(&mut self) { self.or_expr(); }
    fn or_expr(&mut self) {
        self.and_expr();
        while self.is_id("or") { self.p += 1; let l = self.label(); self.e("test %rax, %rax"); self.e(&format!("jnz L{}", l)); self.and_expr(); self.out.push(format!("L{}:\n", l)); self.e("test %rax, %rax"); self.e("setne %al"); self.e("movzbq %al, %rax"); }
    }
    fn and_expr(&mut self) {
        self.not_expr();
        while self.is_id("and") { self.p += 1; let l = self.label(); self.e("test %rax, %rax"); self.e(&format!("jz L{}", l)); self.not_expr(); self.out.push(format!("L{}:\n", l)); self.e("test %rax, %rax"); self.e("setne %al"); self.e("movzbq %al, %rax"); }
    }
    fn not_expr(&mut self) {
        if self.is_id("not") { self.p += 1; self.not_expr(); self.e("test %rax, %rax"); self.e("sete %al"); self.e("movzbq %al, %rax"); } else { self.cmp_expr(); }
    }
    fn cmp_expr(&mut self) {
        self.add_expr();
        let ops = [("==", "sete"), ("!=", "setne"), ("<", "setl"), (">", "setg"), ("<=", "setle"), (">=", "setge")];
        for (o, s) in ops { if self.is_op(o) { self.p += 1; self.e("push %rax"); self.add_expr(); self.e("mov %rax, %rcx"); self.e("pop %rax"); self.e("cmp %rcx, %rax"); self.e(&format!("{} %al", s)); self.e("movzbq %al, %rax"); return; } }
    }
    fn add_expr(&mut self) {
        self.mul_expr();
        loop {
            let op = if self.is_op("+") { "add" } else if self.is_op("-") { "sub" } else { return };
            self.p += 1; self.e("push %rax"); self.mul_expr(); self.e("mov %rax, %rcx"); self.e("pop %rax"); self.e(&format!("{} %rcx, %rax", op));
        }
    }
    fn mul_expr(&mut self) {
        self.unary();
        loop {
            let op = if self.is_op("*") { 0 } else if self.is_op("/") { 1 } else if self.is_op("%") { 2 } else { return };
            self.p += 1; self.e("push %rax"); self.unary(); self.e("mov %rax, %rcx"); self.e("pop %rax");
            if op == 0 { self.e("imul %rcx, %rax"); } else { self.e("cqo"); self.e("idiv %rcx"); if op == 2 { self.e("mov %rdx, %rax"); } }
        }
    }
    fn unary(&mut self) { if self.is_op("-") { self.p += 1; self.unary(); self.e("neg %rax"); } else { self.postfix(); } }
    fn postfix(&mut self) {
        self.primary();
        loop {
            if self.is_op("[") { self.p += 1; self.e("push %rax"); self.expr(); self.expect("]"); self.e("mov %rax, %rcx"); self.e("pop %rax"); self.e("mov (%rax,%rcx,8), %rax"); }
            else if self.is_op(".") { self.p += 1; let ln = self.line(); let f = self.ident(); let k = *self.fields.get(&f).unwrap_or_else(|| die(ln, &format!("unknown field '{}'", f))); self.e(&format!("mov {}(%rax), %rax", 8 * k)); }
            else { return; }
        }
    }
    fn args(&mut self) -> usize { self.expect("("); let mut n = 0; while !self.is_op(")") { self.expr(); self.e("push %rax"); n += 1; if !self.is_op(")") { self.expect(","); } } self.p += 1; n }
    fn primary(&mut self) {
        let ln = self.line();
        match self.tok().clone() {
            T::Num(v) => { self.p += 1; self.e(&format!("mov ${}, %rax", v)); }
            T::Str(s) => { self.p += 1; self.ns += 1; let bytes: Vec<String> = s.bytes().chain(std::iter::once(0)).map(|b| b.to_string()).collect(); self.data.push(format!("S{}: .byte {}\n", self.ns, bytes.join(","))); self.e(&format!("lea S{}(%rip), %rax", self.ns)); }
            T::Op(o) if o == "(" => { self.p += 1; self.expr(); self.expect(")"); }
            T::Op(o) if o == "[" => {
                self.p += 1; let mut n = 0;
                while !self.is_op("]") { self.expr(); self.e("push %rax"); n += 1; if !self.is_op("]") { self.expect(","); } }
                self.p += 1; self.e(&format!("push ${}", n)); self.e("call rt_list"); self.e(&format!("add $8, %rsp"));
                // fill from stack: element i at (n-1-i)*8(%rsp)
                for i in 0..n { self.e(&format!("mov {}(%rsp), %rcx", 8 * (n - 1 - i))); self.e(&format!("mov %rcx, {}(%rax)", 8 * i)); }
                if n > 0 { self.e(&format!("add ${}, %rsp", 8 * n)); }
            }
            T::Id(n) => {
                self.p += 1;
                match n.as_str() { "True" => { self.e("mov $1, %rax"); return; } "False" | "None" => { self.e("mov $0, %rax"); return; } _ => {} }
                if n == "ord" && self.is_op("(") { // ord("c") folds to the byte value (forgec uses it for char codes)
                    if let T::Str(s) = self.t[self.p + 1].0.clone() { if s.len() == 1 { self.p += 3; self.e(&format!("mov ${}, %rax", s.as_bytes()[0])); return; } }
                    die(ln, "ord() needs a one-character string literal");
                }
                if self.is_op("(") {
                    let bi: &[(&str, usize)] = &[("print", 1), ("puts", 1), ("eputs", 1), ("concat", 2), ("write_file", 2), ("read_file", 1), ("len", 1), ("char_at", 2), ("set_byte", 3), ("substr", 3), ("list", 1), ("alloc", 1), ("join", 2), ("argc", 0), ("argv", 1), ("exit", 1), ("str_eq", 2), ("chr", 1), ("str", 1)];
                    let is_str_arg = matches!(self.t[self.p + 1].0, T::Str(_));
                    let nargs = self.args();
                    if let Some((_, k)) = bi.iter().find(|(b, _)| *b == n) {
                        if *k != nargs { die(ln, &format!("{}() takes {} argument(s)", n, k)); }
                        let target = match n.as_str() { "print" => if is_str_arg { "puts" } else { "print_int" }, "str" => "itos", x => x };
                        self.e(&format!("call rt_{}", target));
                    } else { self.calls.push((n.clone(), nargs, ln)); self.e(&format!("call f_{}", n)); }
                    if nargs > 0 { self.e(&format!("add ${}, %rsp", 8 * nargs)); }
                } else {
                    let r = self.var_ref(&n).unwrap_or_else(|| die(ln, &format!("undefined variable '{}'", n)));
                    self.e(&format!("mov {}, %rax", r));
                }
            }
            _ => die(ln, "expected expression"),
        }
    }
    // ---------- statements ----------
    fn block(&mut self) {
        self.expect(":"); if self.tok() != &T::Nl { die(self.line(), "expected newline after ':'"); } self.p += 1;
        if self.tok() != &T::Indent { die(self.line(), "expected indented block"); } self.p += 1;
        while self.tok() != &T::Dedent && self.tok() != &T::Eof { self.stmt(); }
        self.p += 1;
    }
    fn end_stmt(&mut self) { if self.tok() != &T::Nl { die(self.line(), "expected end of line"); } self.p += 1; }
    fn stmt(&mut self) {
        let ln = self.line();
        if self.is_id("def") { self.def(); return; }
        if self.is_id("class") { self.class(); return; }
        if self.is_id("if") {
            let end = self.label();
            loop {
                self.p += 1; self.expr(); let nx = self.label(); self.e("test %rax, %rax"); self.e(&format!("jz L{}", nx));
                self.block(); self.e(&format!("jmp L{}", end)); self.out.push(format!("L{}:\n", nx));
                if self.is_id("elif") { continue; }
                if self.is_id("else") { self.p += 1; self.block(); }
                break;
            }
            self.out.push(format!("L{}:\n", end)); return;
        }
        if self.is_id("while") {
            self.p += 1; let top = self.label(); let end = self.label(); self.out.push(format!("L{}:\n", top));
            self.expr(); self.e("test %rax, %rax"); self.e(&format!("jz L{}", end));
            self.loops.push((top, end)); self.block(); self.loops.pop();
            self.e(&format!("jmp L{}", top)); self.out.push(format!("L{}:\n", end)); return;
        }
        if self.is_id("for") {
            // for v in range(a[, b]):  -> hidden end variable
            self.p += 1; let v = self.ident(); if !self.is_id("in") { die(ln, "expected 'in'"); } self.p += 1;
            if !self.is_id("range") { die(ln, "only 'for x in range(...)' is supported"); } self.p += 1;
            self.expect("("); self.expr(); let vr = self.assign_ref(&v);
            let lim = format!("__lim{}", self.nl + 1); let limn = self.label();
            if self.is_op(",") { self.p += 1; self.e(&format!("mov %rax, {}", vr)); self.expr(); } else { self.e("mov %rax, %rcx"); self.e("mov $0, %rax"); self.e(&format!("mov %rax, {}", vr)); self.e("mov %rcx, %rax"); }
            self.expect(")");
            if !self.infunc { self.globals.push(lim.clone()); self.data.push(format!("g_{}: .quad 0\n", lim)); }
            let lr = self.assign_ref(&lim); self.e(&format!("mov %rax, {}", lr));
            let top = self.label(); let cont = self.label(); let end = self.label(); let _ = limn;
            self.out.push(format!("L{}:\n", top)); self.e(&format!("mov {}, %rax", vr)); self.e(&format!("cmp {}, %rax", lr)); self.e(&format!("jge L{}", end));
            self.loops.push((cont, end)); self.block(); self.loops.pop();
            self.out.push(format!("L{}:\n", cont)); self.e(&format!("incq {}", vr)); self.e(&format!("jmp L{}", top)); self.out.push(format!("L{}:\n", end)); return;
        }
        if self.is_id("return") {
            if !self.infunc { die(ln, "'return' outside function"); }
            self.p += 1; if self.tok() == &T::Nl { self.e("mov $0, %rax"); } else { self.expr(); }
            self.e("leave"); self.e("ret"); self.end_stmt(); return;
        }
        if self.is_id("break") || self.is_id("continue") {
            let b = self.is_id("break"); self.p += 1; let (c, e) = *self.loops.last().unwrap_or_else(|| die(ln, "break/continue outside loop"));
            self.e(&format!("jmp L{}", if b { e } else { c })); self.end_stmt(); return;
        }
        if self.is_id("pass") { self.p += 1; self.end_stmt(); return; }
        if self.is_id("global") { while self.tok() != &T::Nl { self.p += 1; } self.end_stmt(); return; }
        // assignment forms
        if let T::Id(n) = self.tok().clone() {
            let nx = &self.t[self.p + 1].0;
            if *nx == T::Op(":".into()) { self.p += 2; self.skip_type(); if !self.is_op("=") { self.end_stmt(); if self.infunc { self.assign_ref(&n); } return; } }
            else { self.p += 1; }
            if self.is_op("=") { self.p += 1; self.expr(); let r = self.assign_ref(&n); self.e(&format!("mov %rax, {}", r)); self.end_stmt(); return; }
            if self.is_op("+=") || self.is_op("-=") {
                let op = if self.is_op("+=") { "add" } else { "sub" }; self.p += 1; self.expr();
                let r = self.var_ref(&n).unwrap_or_else(|| die(ln, &format!("undefined variable '{}'", n)));
                self.e(&format!("{} %rax, {}", op, r)); self.end_stmt(); return;
            }
            // target[...] = v  or  target.f = v : find '=' at depth 0 on this line
            let mut q = self.p; let mut d = 0; let mut has = false;
            while self.t[q].0 != T::Nl && self.t[q].0 != T::Eof { match &self.t[q].0 { T::Op(o) if o == "(" || o == "[" => d += 1, T::Op(o) if o == ")" || o == "]" => d -= 1, T::Op(o) if o == "=" && d == 0 => { has = true; break; } _ => {} } q += 1; }
            self.p -= 1;
            if has {
                // evaluate base expression up to last accessor
                let r = self.var_ref(&n).unwrap_or_else(|| die(ln, &format!("undefined variable '{}'", n))); self.p += 1;
                self.e(&format!("mov {}, %rax", r));
                loop {
                    let is_last_idx; // determine if accessor is last before '='
                    if self.is_op("[") {
                        self.p += 1; self.e("push %rax"); self.expr(); self.expect("]");
                        is_last_idx = self.is_op("=");
                        if is_last_idx { self.p += 1; self.e("push %rax"); self.expr(); self.e("pop %rcx"); self.e("pop %rdx"); self.e("mov %rax, (%rdx,%rcx,8)"); self.end_stmt(); return; }
                        self.e("mov %rax, %rcx"); self.e("pop %rax"); self.e("mov (%rax,%rcx,8), %rax");
                    } else if self.is_op(".") {
                        self.p += 1; let f = self.ident(); let k = *self.fields.get(&f).unwrap_or_else(|| die(ln, &format!("unknown field '{}'", f)));
                        if self.is_op("=") { self.p += 1; self.e("push %rax"); self.expr(); self.e("pop %rdx"); self.e(&format!("mov %rax, {}(%rdx)", 8 * k)); self.end_stmt(); return; }
                        self.e(&format!("mov {}(%rax), %rax", 8 * k));
                    } else { die(ln, "invalid assignment target"); }
                }
            }
        }
        self.expr(); self.end_stmt();
    }
    fn class(&mut self) {
        // class Name:\n  field: type ...  -> constructor Name(f1, f2, ...) ; fields addressed by name
        let ln = self.line(); self.p += 1; let name = self.ident(); self.expect(":"); self.end_stmt();
        if self.tok() != &T::Indent { die(ln, "expected class body"); } self.p += 1;
        let mut nf = 0;
        while self.tok() != &T::Dedent {
            if self.is_id("pass") { self.p += 1; self.end_stmt(); continue; }
            let f = self.ident(); if self.is_op(":") { self.p += 1; self.skip_type(); } self.end_stmt();
            if let Some(&k) = self.fields.get(&f) { if k != nf { die(ln, &format!("field '{}' must have the same position in every class", f)); } }
            self.fields.insert(f, nf); nf += 1;
        }
        self.p += 1;
        self.classes.insert(name.clone(), nf); self.funcs.insert(name.clone(), nf);
        let skip = self.label(); self.e(&format!("jmp L{}", skip));
        self.out.push(format!("f_{}:\n", name)); self.e(&format!("push ${}", nf)); self.e("call rt_list"); self.e("add $8, %rsp");
        for i in 0..nf { self.e(&format!("mov {}(%rsp), %rcx", 8 * (nf - i))); self.e(&format!("mov %rcx, {}(%rax)", 8 * i)); }
        self.e("ret"); self.out.push(format!("L{}:\n", skip));
    }
    fn def(&mut self) {
        let ln = self.line(); if self.infunc { die(ln, "nested def not supported"); }
        self.p += 1; let name = self.ident(); self.expect("(");
        let mut ps = vec![]; while !self.is_op(")") { ps.push(self.ident()); if self.is_op(":") { self.p += 1; self.skip_type(); } if !self.is_op(")") { self.expect(","); } }
        self.p += 1; if self.is_op("->") { self.p += 1; self.skip_type(); }
        if self.funcs.contains_key(&name) { die(ln, &format!("function '{}' defined twice", name)); }
        self.funcs.insert(name.clone(), ps.len());
        let skip = self.label(); self.e(&format!("jmp L{}", skip));
        self.out.push(format!("f_{}:\n", name)); self.e("push %rbp"); self.e("mov %rsp, %rbp"); self.e("sub $0, %rsp"); let at = self.out.len() - 1;
        self.infunc = true; self.params = ps; self.locals = vec![];
        self.block();
        self.e("mov $0, %rax"); self.e("leave"); self.e("ret");
        let fs = (self.locals.len() * 8 + 15) & !15; self.out[at] = format!("    sub ${}, %rsp\n", fs);
        self.infunc = false; self.out.push(format!("L{}:\n", skip));
    }
}

fn main() {
    let a: Vec<String> = env::args().collect();
    if a.len() < 2 { eprintln!("usage: mylang <file.forge | -e 'source'> [-o out] [-S]"); exit(2); }
    let (src, mut outn) = if a[1] == "-e" { (a[2].clone(), "output".to_string()) } else {
        (fs::read_to_string(&a[1]).unwrap_or_else(|e| { eprintln!("error: cannot read {}: {}", a[1], e); exit(1) }), a[1].trim_end_matches(".forge").to_string()) };
    let mut only_s = false; let mut i = 2; if a[1] == "-e" { i = 3; }
    while i < a.len() { if a[i] == "-o" { outn = a[i + 1].clone(); i += 1; } else if a[i] == "-S" { only_s = true; } i += 1; }
    let t = lex(&src);
    let mut c = C { t, p: 0, out: vec![], data: vec![], nl: 0, ns: 0, globals: vec![], infunc: false, params: vec![], locals: vec![], funcs: HashMap::new(), calls: vec![], loops: vec![], fields: HashMap::new(), classes: HashMap::new() };
    // prescan globals: NAME '=' / NAME ':' at block depth 0, start of statement
    let mut d = 0; let mut sol = true; let mut fd: i32 = -1;
    for k in 0..c.t.len() {
        match &c.t[k].0 { T::Indent => d += 1, T::Dedent => { d -= 1; if d == fd { fd = -1; } } _ => {} }
        if fd < 0 && sol { if let T::Id(n) = &c.t[k].0 {
            if n == "def" || n == "class" { fd = d; }
            else if n == "for" { if let T::Id(v) = &c.t[k + 1].0 { if !c.globals.contains(v) { c.globals.push(v.clone()); } } }
            else { let nx = &c.t[k + 1].0; if (*nx == T::Op("=".into()) || *nx == T::Op(":".into())) && !c.globals.contains(n) && n != "else" { c.globals.push(n.clone()); } } } }
        sol = matches!(c.t[k].0, T::Nl | T::Indent | T::Dedent);
    }
    c.out.push(".text\n.globl _start\n_start:\n".into());
    c.e("mov (%rsp), %rax"); c.e("mov %rax, rt_argcv(%rip)"); c.e("lea 8(%rsp), %rax"); c.e("mov %rax, rt_argvp(%rip)");
    c.e("lea rt_heap(%rip), %rax"); c.e("mov %rax, rt_hp(%rip)");
    while c.tok() != &T::Eof { c.stmt(); }
    c.e("mov $0, %rdi"); c.e("mov $60, %rax"); c.e("syscall");
    for (n, k, ln) in c.calls.clone() { match c.funcs.get(&n) { None => die(ln, &format!("undefined function '{}'", n)), Some(&m) if m != k => die(ln, &format!("{}() takes {} argument(s), got {}", n, m, k)), _ => {} } }
    let mut asm = c.out.concat();
    asm.push_str(include_str!("runtime.s"));
    asm.push_str(".data\n");
    for g in &c.globals { if !g.starts_with("__lim") { asm.push_str(&format!("g_{}: .quad 0\n", g)); } }
    asm.push_str(&c.data.concat());
    let sfile = format!("{}.s", outn);
    fs::write(&sfile, asm).expect("write asm");
    if only_s { return; }
    let ofile = format!("{}.o", outn);
    let ok = Command::new("as").args(["-o", &ofile, &sfile]).status().map(|s| s.success()).unwrap_or(false)
        && Command::new("ld").args(["-o", &outn, &ofile]).status().map(|s| s.success()).unwrap_or(false);
    let _ = fs::remove_file(&ofile);
    if !ok { eprintln!("error: assembling/linking failed"); exit(1); }
}
