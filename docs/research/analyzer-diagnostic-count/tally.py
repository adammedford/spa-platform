# Tally the warnings-only logs of #588's clean builds into one CSV per (setting, rule, project).
import re, csv, sys, pathlib, collections
C = pathlib.Path(sys.argv[1]); WT = sys.argv[2].rstrip('/') + '/'
S = '/usr/local/share/dotnet/sdk/10.0.401/Sdks/Microsoft.NET.Sdk'
cat, src = {}, {}
for f in ('sdk-rules.csv', 'thirdparty-rules.csv'):
    for pkg, rid, category, sev, on in csv.reader(open(C / f)):
        cat.setdefault(rid, category); src.setdefault(rid, pkg)
def warn_ids(*files):
    ids = set()
    for f in files:
        ids |= set(re.findall(r'dotnet_diagnostic\.(\w+)\.severity = warning', open(f).read()))
    return ids
sdk_on = warn_ids(f'{S}/analyzers/build/config/analysislevel_10_all.globalconfig',
                  f'{S}/analyzers/build/config/analysislevelsecurity_10_all.globalconfig',
                  f'{S}/codestyle/cs/build/config/analysislevelstyle_all.globalconfig')
tp_on = warn_ids(C / 's3.globalconfig')
line = re.compile(r'^(?P<file>.*?)(?:\((?P<pos>\d+,\d+)\))?\s*:\s*warning (?P<id>[A-Za-z]+\d+): (?P<msg>.*) \[(?P<proj>[^\]]+)\]$')
def tier(proj):
    rel = proj.replace(WT, '')
    shipping = rel.startswith('src/Gateway/') or (rel.startswith('src/Platform.') and not rel.startswith('src/Platform.Testing/'))
    return rel.split('/')[-1].removesuffix('.csproj'), ('shipping' if shipping else 'test')
rows = collections.Counter(); projects = {}
for s in ('S0', 'S1', 'S2', 'S3'):
    seen = set()
    for l in open(C / 'runs' / f'{s}.log', errors='replace'):
        m = line.match(l.strip())
        if not m: continue
        # One finding per (file, position, id); a file-less finding (CSC, a config warning) per (project, id, message).
        key = (m['file'], m['pos'], m['id']) if m['pos'] else (m['proj'], m['id'], m['msg'])
        if key in seen: continue
        seen.add(key)
        p, t = tier(m['proj']); projects[p] = t
        rows[(s, m['id'], p, t)] += 1
def kind(rid):
    if rid.startswith('IDE'): return 'SDK-CodeStyle', 'Style'
    return src.get(rid, 'SDK-compiler' if rid.startswith('CS') else 'other'), cat.get(rid, '')
with open(C / 'analyzer-diagnostic-count.csv', 'w', newline='') as f:
    w = csv.writer(f); w.writerow(['setting', 'rule', 'source', 'category', 'project', 'tier', 'count'])
    for (s, rid, p, t), n in sorted(rows.items()):
        w.writerow([s, rid, *kind(rid), p, t, n])
# Rules enabled at warning in S3 that fired nowhere: free to set to error today.
fired = {rid for (s, rid, p, t) in rows if s == 'S3'}
with open(C / 'analyzer-zero-rules.csv', 'w', newline='') as f:
    w = csv.writer(f); w.writerow(['rule', 'source', 'category'])
    for rid in sorted((sdk_on | tp_on) - fired):
        w.writerow([rid, *kind(rid)])
print(len(rows), 'rows;', len(projects), 'projects;', len((sdk_on | tp_on) - fired), 'enabled-and-clean rules')
