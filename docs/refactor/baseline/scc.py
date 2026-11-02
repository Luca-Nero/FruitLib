"""Class-level and folder-level dependency analysis over internal_graph.json: cycles, fan-in, fan-out, layer breaches."""
import json, sys, collections

src = sys.argv[1]
graph = json.load(open(src + "/internal_graph.json", encoding="utf-8"))
api = json.load(open(src + "/public_api.json", encoding="utf-8"))

# Class = the file stem up to the first dot (FruitBodyControl.Hands -> FruitBodyControl); small value types collapse into their owning file's class.
def owner(path):
    return path.rsplit("/", 1)[1][:-3].split(".")[0]
def folder(path):
    return path.split("/")[0]

type_to_file = {}
for rel in graph:
    type_to_file[owner(rel)] = rel
# Types declared in a differently named file: read the public API for the rest, last resort keep the name.
for row in api:
    t = row["type"].split(".")[0]
    type_to_file.setdefault(t, row["file"])
aliases = {}
for row in api:
    t = row["type"].split(".")[-1]
    aliases.setdefault(t, owner(row["file"]))

edges = collections.defaultdict(set)
folder_edges = collections.Counter()
folder_detail = collections.defaultdict(set)
for rel, refs in graph.items():
    a = owner(rel)
    edges[a]
    for r in refs:
        b = aliases.get(r, r)
        if b == a:
            continue
        edges[a].add(b)
        bfile = type_to_file.get(b)
        if bfile and folder(bfile) != folder(rel):
            folder_edges[(folder(rel), folder(bfile))] += 1
            folder_detail[(folder(rel), folder(bfile))].add(f"{a}->{b}")

# Tarjan.
index = {}; low = {}; onstack = set(); stack = []; sccs = []; counter = [0]
sys.setrecursionlimit(10000)
def strong(v):
    index[v] = low[v] = counter[0]; counter[0] += 1
    stack.append(v); onstack.add(v)
    for w in edges.get(v, ()):
        if w not in index:
            strong(w); low[v] = min(low[v], low[w])
        elif w in onstack:
            low[v] = min(low[v], index[w])
    if low[v] == index[v]:
        comp = []
        while True:
            w = stack.pop(); onstack.discard(w); comp.append(w)
            if w == v:
                break
        sccs.append(sorted(comp))
for v in list(edges):
    if v not in index:
        strong(v)

print("== cycles (strongly connected class groups) ==")
for c in sorted((s for s in sccs if len(s) > 1), key=len, reverse=True):
    print(len(c), c)

print("\n== class fan-in (who depends on it), top 20 ==")
fanin = collections.Counter()
for a, bs in edges.items():
    for b in bs:
        fanin[b] += 1
for k, v in fanin.most_common(20):
    print(v, k)

print("\n== class fan-out, top 15 ==")
for k, bs in sorted(edges.items(), key=lambda kv: -len(kv[1]))[:15]:
    print(len(bs), k)

print("\n== folder edges ==")
for (a, b), n in sorted(folder_edges.items()):
    print(f"{a} -> {b}: {n}  {sorted(folder_detail[(a, b)])}")

print("\n== production code reaching into Debug/ ==")
for rel, refs in graph.items():
    if rel.startswith("Debug/"):
        continue
    hits = [r for r in refs if type_to_file.get(aliases.get(r, r), "").startswith("Debug/")]
    if hits:
        print(rel, hits)

print("\n== public members per class, top 25 ==")
perclass = collections.Counter(owner(r["file"]) for r in api if r["kind"] not in ("enum-values",))
for k, v in perclass.most_common(25):
    print(v, k)
print("total public rows", len(api), "public types", sum(1 for r in api if r["kind"] in ("class", "struct", "interface", "enum", "record", "delegate")), "obsolete", sum(1 for r in api if r["obsolete"]))
