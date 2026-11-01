def main():
    words = []
    i = 0
    while i < 200000:
        words.append("w" + str(i % 997))
        i += 1
    line = " ".join(words)
    parts = line.split(" ")
    total = 0
    for p in parts:
        if p.startswith("w9"):
            total += len(p)
        total += p.find("7")
    up = line.upper()
    print(total, len(up), up.count("W1"))
main()
