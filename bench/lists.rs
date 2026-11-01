fn run(n: usize) -> i64 { let mut xs = vec![0i64; n]; for i in 0..n { xs[i] = (i as i64) * 3 % 1000; } let mut t = 0; for _ in 0..10 { for i in 0..n { t += xs[i]; } } t }
fn main() { let n = 9999999 + std::env::args().count(); println!("{}", run(n)); }
