def run(n):
    parts = [None] * n
    for i in range(n):
        parts[i] = str(i) + ","
    s = "".join(parts)
    return len(s)
print(run(1000000))
