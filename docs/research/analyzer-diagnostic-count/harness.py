# Timed builds for #588. Settings interleaved per round; restore is untimed.
import subprocess, time, json, os, sys, pathlib
C = pathlib.Path(sys.argv[1]); WT = pathlib.Path(sys.argv[2]); OUT = C / 'runs'; OUT.mkdir(exist_ok=True)
SLN = 'SpaPlatform.slnx'
BASE = ['-p:TreatWarningsAsErrors=false']
SDK = ['-p:AnalysisMode=All', '-p:AnalysisLevelSecurity=latest-all', '-p:EnforceCodeStyleInBuild=true', '-p:AnalysisLevelStyle=latest-all']
SET = {
  'S0': (BASE, None),
  'S1': (BASE + SDK, None),
  'S2': (BASE + SDK + [f'-p:CustomAfterMicrosoftCommonProps={C}/s2.props'], f'{C}/s2.props'),
  'S3': (BASE + SDK + [f'-p:CustomAfterMicrosoftCommonProps={C}/s3.props'], f'{C}/s2.props'),
}
results = []
def restore(name):
    extra = [f'-p:CustomAfterMicrosoftCommonProps={SET[name][1]}'] if SET[name][1] else []
    subprocess.run(['dotnet', 'restore', SLN, '-v:q'] + extra, cwd=WT, check=True, capture_output=True)
def build(name, kind, rnd, args, log=None):
    cmd = ['dotnet', 'build', SLN, '--no-restore'] + args + SET[name][0]
    if log: cmd += ['-fl', f'-flp:logfile={log};warningsonly']
    t = time.monotonic(); p = subprocess.run(cmd, cwd=WT, capture_output=True, text=True); dt = time.monotonic() - t
    r = dict(setting=name, kind=kind, round=rnd, seconds=round(dt, 2), exit=p.returncode)
    results.append(r); print(json.dumps(r), flush=True)
    (OUT / 'results.json').write_text(json.dumps(results, indent=1))
touch = WT / 'src/Gateway/Program.cs'
# Cold: fresh MSBuild nodes, no compiler server.
for name in SET:
    restore(name)
    build(name, 'cold-clean', 0, ['--no-incremental', '-nodeReuse:false', '-p:UseSharedCompilation=false'])
for rnd in (1, 2, 3):
    for name in SET:
        restore(name)
        build(name, 'clean', rnd, ['--no-incremental'], OUT / f'{name}.log' if rnd == 1 else None)
        build(name, 'noop', rnd, [])
        touch.touch(); build(name, 'touch-gateway', rnd, [])
# Per-analyzer execution time at the broadest setting.
restore('S3')
p = subprocess.run(['dotnet', 'build', SLN, '--no-restore', '--no-incremental', '-p:ReportAnalyzer=true', '-v:d'] + SET['S3'][0], cwd=WT, capture_output=True, text=True)
(OUT / 'S3-reportanalyzer.log').write_text(p.stdout)
print('done', flush=True)
