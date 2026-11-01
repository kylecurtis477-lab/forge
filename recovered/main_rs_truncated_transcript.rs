use std::collections::HashMap;
use std::env;
use std::fs;
use std::process::Command;

#[derive(Debug, Clone, PartialEq)]
enum Token {
    Number(i64), String(String), Ident(String),
    Plus, Minus, Star, Slash, LParen, RParen, LBracket, RBracket,
    Eq, Colon, Comma, Gt, Lt, Ge, Le, EqEq, NotEq,
    Indent, Dedent, Newline, Eof,
    Def, If, Else, While, Print, Return,
}

struct Lexer {
    input: Vec<char>,
    pos: usize,
    line: usize,
    indent_stack: Vec<usize>,
    at_line_start: bool,
}
impl Lexer {
    fn new(input: &str) -> Self {
        Self { input: input.chars().collect(), pos: 0, line: 1, indent_stack: vec![0], at_line_start: true }
    }
    fn next_token(&mut self) -> Token {
        if self.at_line_start { self.at_line_start = false; return self.handle_indent(); }
        while self.pos < self.input.len() && self.input[self.pos] == ' ' { self.pos += 1; }
        if self.pos >= self.input.len() {
            if self.indent_stack.len() > 1 { self.indent_stack.pop(); return Token::Dedent; }
            return Token::Eof;
        }
        let c = self.input[self.pos];
        if c == '\n' { self.pos += 1; self.line += 1; self.at_line_start = true; return Token::Newline; }
        if c == '#' { while self.pos < self.input.len() && self.input[self.pos] != '\n' { self.pos += 1; } return self.next_token(); }
        if c.is_ascii_alphabetic() || c == '_' {
            let start = self.pos;
            while self.pos < self.input.len() && (self.input[self.pos].is_ascii_alphanumeric() || self.input[self.pos] == '_') { self.pos += 1; }
            let ident: String = self.input[start..self.pos].iter().collect();
            return match ident.as_str() {
                "def" => Token::Def, "if" => Token::If, "else" => Token::Else,
                "while" => Token::While, "print" => Token::Print, "return" => Token::Return,
                _ => Token::Ident(ident),
            };
        }
        if c == '"' {
            self.pos += 1; let start = self.pos;
            while self.pos < self.input.len() && self.input[self.pos] != '"' { self.pos += 1; }
            let s: String = self.input[start..self.pos].iter().collect();
            if self.pos < self.input.len() { self.pos += 1; }
            return Token::String(s);
        }
        self.pos += 1;
        match c {
            '+' => Token::Plus, '-' => Token::Minus, '*' => Token::Star, '/' => Token::Slash,
            '(' => Token::LParen, ')' => Token::RParen, '[' => Token::LBracket, ']' => Token::RBracket,
            ':' => Token::Colon, ',' => Token::Comma,
            '=' => { if self.pos < self.input.len() && self.input[self.pos] == '=' { self.pos += 1; Token::EqEq } else { Token::Eq } }
            '>' => { if self.pos < self.input.len() && self.input[self.pos] == '=' { self.pos += 1; Token::Ge } else { Token::Gt } }
            '<' => { if self.pos < self.input.len() && self.input[self.pos] == '=' { self.pos += 1; Token::Le } else { Token::Lt } }
            '!' => { if self.pos < self.input.len() && self.input[self.pos] == '=' { self.pos += 1; Token::NotEq } else { panic!("line {}: unexpected !", self.line) } }
            '0'..='9' => {
                let mut num = (c as u8 - b'0') as i64;
                while self.pos < self.input.len() && self.input[self.pos].is_ascii_digit() {
                    num = num * 10 + (self.input[self.pos] as u8 - b'0') as i64; self.pos += 1;
                }
                Token::Number(num)
            }
            _ => panic!("line {}: unexpected character '{}'", self.line, c),
        }
    }
    fn handle_indent(&mut self) -> Token {
        let mut spaces = 0;
        while self.pos < self.input.len() && self.input[self.pos] == ' ' { spaces += 1; self.pos += 1; }
        if self.pos < self.input.len() && self.input[self.pos] == '\n' { self.pos += 1; self.line += 1; self.at_line_start = true; return self.next_token(); }
        let current = *self.indent_stack.last().unwrap();
        if spaces > current { self.indent_stack.push(spaces); Token::Indent }
        else if spaces < current { self.indent_s
... 