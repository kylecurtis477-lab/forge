fn run(n: usize) -> usize { let mut parts = vec![String::new(); n]; for i in 0..n { parts[i] = i.to_string() + ","; } let s = parts.concat(); s.len() }
fn main() { let n = 999999 + std::env::args().count(); println!("{}", run(n)); }
