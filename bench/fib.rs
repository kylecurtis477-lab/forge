fn fib(n: i64) -> i64 { if n < 2 { n } else { fib(n - 1) + fib(n - 2) } }
fn main() { let n = std::env::args().count() as i64 + 31; println!("{}", fib(n)); }
