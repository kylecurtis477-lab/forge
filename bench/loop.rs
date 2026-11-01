fn run(n: i64) -> i64 { let mut s = 0; let mut i = 0; while i < n { s += i * i % 7; i += 1; } s }
fn main() { let n = 99999999 + std::env::args().count() as i64; println!("{}", run(n)); }
