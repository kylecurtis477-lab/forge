fn main(){let n=3000000;let(mut x,mut v,mut s)=(0.0f64,1.0f64,0.0f64);let mut i=0;while i<n{let a=-0.001*x;v=v+a*0.01;x=x+v*0.01;s=s+x*x;i+=1;}println!("{}",s as i64);}
