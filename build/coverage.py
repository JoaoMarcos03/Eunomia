"""Cobertura de linhas por projeto de src/ e limiares que só sobem (roadmap 2.2).

Lê todos os *.cobertura.xml de uma pasta (o coletor "Code Coverage" do Microsoft.NET.Test.Sdk, com
`dotnet test --collect "Code Coverage;Format=cobertura"`), junta as linhas de várias corridas (testes unitários e de
integração: uma linha conta como coberta se alguma corrida a executou) e agrupa por projeto pelo caminho do ficheiro
(src/<Projeto>/...). Código gerado (obj/) e o que está fora de src/ (testes, pacotes) fica de fora.

Os limiares estão em build/coverage-baseline.json ({"Projeto": percentagem}); só os projetos listados são verificados
(começa pelo core: Engine.*, Drivers.*, App). Um projeto abaixo do limiar menos a tolerância faz falhar; acima do
limiar mais 1 ponto, o relatório sugere subi-lo (--update grava os valores atuais, arredondados para baixo).

Uso:
    python build/coverage.py <pasta> [--baseline build/coverage-baseline.json] [--summary relatorio.md] [--update]
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
    """src/<Projeto>/... -> Projeto deste repositório; None fora de src/, em código gerado (obj/) ou nos pacotes."""
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
    """{projeto: {(ficheiro, linha): coberta}} a partir de todos os relatórios da pasta."""
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
        print("Nenhum *.cobertura.xml em " + args.folder, file=sys.stderr)
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
                status = "abaixo do limiar"
                failures.append(f"{project}: {percent:.1f}% < {threshold:.1f}%")
            elif percent > threshold + 1:
                status = f"subir o limiar para {math.floor(percent * 10) / 10:.1f}"
                raises.append(project)
            else:
                status = "ok"
        shown = "" if threshold is None else f"{threshold:.1f}%"
        rows.append(f"| {project} | {valid} | {covered} | {percent:.1f}% | {shown} | {status} |")
    missing = [p for p in baseline if p not in lines]
    for project in missing:
        failures.append(f"{project}: sem dados de cobertura (projeto removido ou renomeado? atualizar o limiar)")

    report = "\n".join([
        f"## Cobertura de linhas por projeto ({reports} relatório(s))",
        "",
        "| Projeto | Linhas | Cobertas | Cobertura | Limiar | Estado |",
        "| --- | ---: | ---: | ---: | ---: | --- |",
        *rows,
        "",
        f"Limiares em `{args.baseline.replace(os.sep, '/')}`; tolerância de {TOLERANCE} pontos.",
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
        print("Limiares atualizados em " + args.baseline)
        return 0
    if failures:
        print("\nCobertura abaixo do limiar:\n  " + "\n  ".join(failures), file=sys.stderr)
        return 1
    if raises:
        print("\nCobertura acima do limiar em " + ", ".join(raises) + ": subir o limiar (--update).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
