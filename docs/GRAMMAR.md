# Forge grammar (frozen, version 1)

This is the normative grammar of Forge source. It is the grammar accepted by the strict
AST parser (`forgec F out --emit=ast-json`, the `a_*` functions in selfhost/compiler.forge);
every rule below names the function that implements it. The code-generating parser accepts
the same language plus a few legacy forms. A program outside this grammar is rejected with
`expected ...` (diagnostic code E0105). Changes need a version bump of this file and of the
AST schema (`"forge_ast"`).

Notation: `X*` zero or more, `X+` one or more, `[X]` optional, `|` alternatives, `'x'`
literal token, CAPS terminal token classes. Keywords are reserved only where a rule names
them; `include`, `extern`, `struct`, `from`, `as` are contextual.

## Lexical structure

```
NEWLINE   end of a logical line; ';' is also a NEWLINE. Lines inside (), [], {} join.
INDENT / DEDENT   from changes in leading spaces (a tab is not indentation).
COMMENT   '#' to end of line; ignored, except lines starting with "# forge:" (pragmas,
          which select code-generation modes and are kept in the AST as "pragmas").
NAME      [A-Za-z_][A-Za-z0-9_]*
NUMBER    decimal digits | 0x hex digits, '_' separators allowed (no 0b / 0o);
          decimals beyond 64 bits become big integers.
FLOAT     digits '.' digits [exponent] | digits exponent
STRING    '...' | "..." | '''...''' | """...""" with \ escapes (no r/b prefixes);
          adjacent STRINGs concatenate. f"..{expr}.." is desugared by the lexer to
          ("" + ".." + str(expr) + ..).
OP        + - * / // % @ ** << >> & | ^ ~ < > <= >= == != = += -= *= /= //= %= **=
          <<= >>= &= |= ^= ( ) [ ] { } , : . -> ; '@' (decorator; there is no '@=')
```

## Module and statements

```
module      = (NEWLINE | stmt)* EOF                                   (ast_parse)
stmt        = decorated | test_block | if_stmt | while_stmt | for_stmt | try_stmt
            | with_stmt | simple_stmt                                 (a_stmt)
test_block  = 'test' STRING ':' suite     (v1.1, soft keyword: only when followed by STRING ':';
                                           code generation accepts it only at column 0, top level)
decorated   = ('@' test NEWLINE)* (funcdef | externdef | classdef)
funcdef     = 'def' NAME '(' params(typed) ')' ['->' test] block      (a_def)
externdef   = 'extern' 'def' NAME '(' params(typed) ')' ['->' test] end
suite       = block
classdef    = 'class' NAME ['(' args ')'] block
            | 'struct' NAME block            (struct only when followed by NAME ':')
if_stmt     = 'if' test block ('elif' test block)* ['else' block]     (a_if)
while_stmt  = 'while' test block ['else' block]
for_stmt    = 'for' targets 'in' exprlist block ['else' block]
try_stmt    = 'try' block handler* ['else' block] ['finally' block]   (at least one handler or finally)
handler     = 'except' [test ['as' NAME]] block
with_stmt   = 'with' with_item (',' with_item)* block
with_item   = test ['as' target1]
block       = ':' simple_stmt
            | ':' NEWLINE NEWLINE* INDENT (NEWLINE | stmt)+ DEDENT    (a_block)

simple_stmt = small end                                               (a_simple)
end         = NEWLINE | (before DEDENT / EOF)
small       = 'pass' | 'break' | 'continue'
            | 'return' [exprlist]
            | 'raise' [test ['from' test]]
            | ('global' | 'nonlocal') NAME (',' NAME)*
            | 'import' dotted_as (',' dotted_as)*
            | 'from' dotted 'import' ('*' | ['('] name_as (',' name_as)* [','] [')'])
            | 'assert' test [',' test]
            | 'del' exprlist
            | 'include' STRING
            | yield_expr
            | exprlist ('=' (yield_expr | exprlist))+                (Assign)
            | exprlist AUGOP (yield_expr | exprlist)                 (AugAssign)
            | exprlist ':' test ['=' (yield_expr | exprlist)]        (AnnAssign)
            | exprlist                                               (Expr)
AUGOP       = '+=' | '-=' | '*=' | '/=' | '//=' | '%=' | '**=' | '<<=' | '>>=' | '&=' | '|=' | '^='
dotted      = '.'* NAME ('.' NAME)*
dotted_as   = dotted ['as' NAME]
name_as     = NAME ['as' NAME]
```

## Expressions (lowest to highest precedence)

```
exprlist    = test_or_star (',' test_or_star)* [',']                  (a_exprlist; a comma makes a Tuple)
test_or_star= '*' or_expr | test
test        = lambda | or_test ['if' or_test 'else' test]             (a_test)
lambda      = 'lambda' params(untyped) ':' test
or_test     = and_test ('or' and_test)*
and_test    = not_test ('and' not_test)*
not_test    = 'not' not_test | comparison
comparison  = or_expr (compop or_expr)*                               (one Compare node per chain)
compop      = '<' | '>' | '==' | '!=' | '<=' | '>=' | 'in' | 'not' 'in' | 'is' ['not']
or_expr     = xor_expr ('|' xor_expr)*
xor_expr    = and_expr ('^' and_expr)*
and_expr    = shift ('&' shift)*
shift       = arith (('<<' | '>>') arith)*
arith       = term (('+' | '-') term)*
term        = factor (('*' | '/' | '//' | '%' | '@') factor)*
factor      = ('-' | '+' | '~') factor | power
power       = primary ['**' factor]                                   (right-associative)
primary     = atom trailer*
trailer     = '(' args ')' | '[' subscript ']' | '.' NAME
subscript   = sliceitem (',' sliceitem)* [',']
sliceitem   = test | [test] ':' [test] [':' [test]]
args        = [arg (',' arg)* [',']]
arg         = '*' test | '**' test | NAME '=' test | test [comp_clauses]   (a generator only as the first arg)
atom        = NAME | NUMBER | FLOAT | STRING+ | 'True' | 'False' | 'None'
            | '(' ')' | '(' yield_expr ')'
            | '(' test_or_star comp_clauses ')'                      (GenExp)
            | '(' test_or_star ')'
            | '(' test_or_star ',' [test_or_star (',' test_or_star)* [',']] ')'   (Tuple)
            | '[' [test_or_star (comp_clauses | (',' test_or_star)* [','])] ']'
            | '{' '}'
            | '{' test ':' test comp_clauses '}'                     (DictComp)
            | '{' dict_item (',' dict_item)* [','] '}'
            | '{' test_or_star (comp_clauses | (',' test_or_star)* [',']) '}'   (Set / SetComp)
dict_item   = test ':' test | '**' or_expr
comp_clauses= 'for' targets 'in' or_test ('for' targets 'in' or_test | 'if' or_test)*
yield_expr  = 'yield' 'from' test | 'yield' [exprlist]
targets     = target1 (',' target1)* [',']
target1     = '*' or_expr | or_expr
params(t)   = [param (',' param)* [',']]
param       = ['*' | '**'] NAME [':' test if t] ['=' test if no star] | '*'
```

## Notes
- Type annotations are ordinary expressions (`list[int]`, `Callable[[int], int]`,
  `tuple[int, str]`), so the grammar has no separate type language; the type checker
  interprets them.
- `yield` is only valid as a statement, on the right of an assignment, or parenthesised.
- Freestanding programs (`# forge: no-runtime`) use the same grammar; `extern def` and
  `struct` are mostly used there.

## Conformance
- `tests/grammar/valid/*.forge` must parse; `tests/grammar/invalid/*.forge` must be
  rejected with E0105-class diagnostics (`tests/grammar/run.sh`).
- Every example, kernel source, tool, test and the compiler itself must parse
  (tests/ast/roundtrip.sh).
- `tools/grammar_fuzz.forge` (port of the reference `tools/grammar_fuzz.py`) generates random programs from this grammar (must parse and
  round-trip) and mutations that break a rule in a known way (must be rejected).

## Diagnostics note: mismatched closers in call arguments
A call whose argument list hits a closer that does not match, for example `f(}` or `f(1]2)`, used to hang the
compiler: `skip_arg` stops at a mismatched closer at depth 0, and the argument loops (`count_pos`,
`need_slots`, `push_slots`) never advanced. The loops now go through `arg_step`, which reports
`error: line N: expected ')', found '<tok>'` and stops. Regression tests: `tests/errors/call_stray_*.forge`.
`run_tests.sh` also runs every error test under `timeout 20`, so a future hang fails the test instead of
stalling the suite.
