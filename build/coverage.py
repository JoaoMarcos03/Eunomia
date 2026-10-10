"""Line coverage per src/ project, with thresholds that only go up (roadmap 2.2).

Reads every *.cobertura.xml in a folder (the "Code Coverage" collector of Microsoft.NET.Test.Sdk, with
`dotnet test --collect "Code Coverage;Format=cobertura"`), merges the lines of several runs (unit and integration
tests: a line counts as covered if any run executed it) and groups them by project from the file path
(src/<Project>/...). Generated code (obj/) and anything outside src/ (tests, packages) is left out.

The thresholds are in build/coverage-baseline.json ({"Project": percentage}); only the listed projects are checked
(it starts with the core: Engine.*, Drivers.*, App). A project below its threshold minus the tolerance fails; above the
threshold plus 1 point, the report suggests raising it (--update writes the current values, rounded down).

Usage:
    python build/coverage.py <folder> [--baseline build/coverage-baseline.json] [--summary report.md] [--update]
"""

import argparse
import json
import math
import os
import sys
import xml.etree.ElementTree as ET

TOLERANCE = 0.5
SRC = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src")
PROJECTS = {name for name in os.listdir(SRC) if os.path.isfile(os.path.join(SRC, name, name + ".csproj"))}


def project_of(filename):
    """src/<Project>/... -> a project of this repository; None outside src/, in generated code (obj/) or in packages."""
    path = filename.replace("\\", "/")
    marker = "/src/"
    index = path.rfind(marker)
    if index < 0:
        return None, None
    relative = path[index + len(marker):]
    parts = relative.split("/")
    if len(parts) < 2 or parts[0] not in PROJECTS or "obj" in parts or "bin" in parts:
        return None, None
    return parts[0], relative


def collect(folder):
    """{project: {(file, line): covered}} from every report in the folder."""
    lines = {}
    reports = 0
    for root, _, files in os.walk(folder):
        for name in files:
            if not name.endswith(".cobertura.xml"):
                continue
            reports += 1
            tree = ET.parse(os.path.join(root, name))
            for cls in tree.iter("class"):
                project, relative = project_of(cls.get("filename", ""))
                if project is None:
                    continue
                own = lines.setdefault(project, {})
                for line in cls.findall("./lines/line"):
                    key = (relative, int(line.get("number")))
                    own[key] = own.get(key, False) or int(line.get("hits", "0")) > 0
    return lines, reports


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("folder")
    parser.add_argument("--baseline", default=os.path.join("build", "coverage-baseline.json"))
    parser.add_argument("--summary")
    parser.add_argument("--update", action="store_true")
    args = parser.parse_args()

    lines, reports = collect(args.folder)
    if reports == 0:
        print("No *.cobertura.xml in " + args.folder, file=sys.stderr)
        return 2
    with open(args.baseline, encoding="utf-8") as f:
        baseline = json.load(f)

    rows = []
    failures = []
    raises = []
    current = {}
    for project in sorted(lines):
        valid = len(lines[project])
        covered = sum(1 for hit in lines[project].values() if hit)
        percent = 100.0 * covered / valid if valid else 100.0
        current[project] = percent
        threshold = baseline.get(project)
        status = ""
        if threshold is not None:
            if percent < threshold - TOLERANCE:
                status = "below threshold"
                failures.append(f"{project}: {percent:.1f}% < {threshold:.1f}%")
            elif percent > threshold + 1:
                status = f"raise the threshold to {math.floor(percent * 10) / 10:.1f}"
                raises.append(project)
            else:
                status = "ok"
        shown = "" if threshold is None else f"{threshold:.1f}%"
        rows.append(f"| {project} | {valid} | {covered} | {percent:.1f}% | {shown} | {status} |")
    missing = [p for p in baseline if p not in lines]
    for project in missing:
        failures.append(f"{project}: no coverage data (project removed or renamed? update the threshold)")

    report = "\n".join([
        f"## Line coverage per project ({reports} report(s))",
        "",
        "| Project | Lines | Covered | Coverage | Threshold | Status |",
        "| --- | ---: | ---: | ---: | ---: | --- |",
        *rows,
        "",
        f"Thresholds in `{args.baseline.replace(os.sep, '/')}`; tolerance of {TOLERANCE} points.",
    ])
    print(report)
    if args.summary:
        with open(args.summary, "a", encoding="utf-8") as f:
            f.write(report + "\n")

    if args.update:
        updated = {p: math.floor(current[p] * 10) / 10 for p in baseline if p in current}
        with open(args.baseline, "w", encoding="utf-8", newline="\n") as f:
            json.dump(updated, f, indent=2, sort_keys=True)
            f.write("\n")
        print("Thresholds updated in " + args.baseline)
        return 0
    if failures:
        print("\nCoverage below the threshold:\n  " + "\n  ".join(failures), file=sys.stderr)
        return 1
    if raises:
        print("\nCoverage above the threshold in " + ", ".join(raises) + ": raise the threshold (--update).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
