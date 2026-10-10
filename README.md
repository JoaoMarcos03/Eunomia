<h1 align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/brand/eunomia-lockup-dark.svg">
    <source media="(prefers-color-scheme: light)" srcset="assets/brand/eunomia-lockup-light.svg">
    <img alt="Eunomia" src="assets/brand/eunomia-lockup-light.svg" width="410">
  </picture>
</h1>

Cliente de bases de dados para Windows, macOS e Linux, sucessor do HeidiSQL: mesma experiência de utilização, motor novo orientado a performance, estabilidade, concorrência e grandes datasets, e feito para não estragar dados de produção (sessões read-only, avisos de transações abertas, confirmação antes de perder dados). MySQL e MariaDB primeiro; PostgreSQL previsto na arquitetura desde o início.

Estado: **beta 1.0.0-beta.1** (fases 1–9 e 12–15; detalhe em `docs/progress.md`). Pacotes para Windows (x64), macOS (Apple Silicon e Intel) e Linux (x64 e arm64) gerados pelo CI; versões em [Releases](https://github.com/JoaoMarcos03/Eunomia/releases); guia do utilizador em `docs/user-guide.md`; o que vem a seguir em `docs/roadmap.md`.

## Conteúdo

| Caminho | O que é |
| --- | --- |
| `CONTRIBUTING.md` | Preparação do ambiente, regras de arquitetura e de segurança dos dados, padrões de código, testes e processo |
| `CHANGELOG.md` | Alterações visíveis por versão |
| `docs/spec/specification.md` | Especificação técnica completa (30 secções): inventário do HeidiSQL, requisitos de familiaridade, arquitetura, stack, MVP, roadmap, decisões e riscos |
| `docs/spec/appendices/` | Anexos A a G: relatórios de investigação em inglês, com permalinks para o código do HeidiSQL no commit `c123ccdb` |
| `docs/performance.md` | Desempenho em servidores remotos: princípios, ligações de uma sessão, números e como medir |
| `docs/security-audit.md` | Auditoria de segurança do código (9 out 2026) |

A fonte de verdade da especificação é `docs/spec/specification.md`. A versão original, fora do repositório, fica como snapshot de 30 set 2026, anterior às decisões D9 e D10, e não volta a ser atualizada.

## Decisões pendentes

1. Licença (ADR-16): por decidir. O objetivo é gerar receita, com doações ou uma edição Enterprise paga. Até à decisão: não copiar código do HeidiSQL (GPL-2.0), o copyright fica todo do autor (contribuições externas só com CLA) e o repo fica privado.
2. Experiência da equipa: C# ou C++ (C++ forte muda a recomendação para Qt 6).
3. Orçamento para code signing e crash reporting.

Decididas a 30 set 2026: D9 e D10 (ADR-19).

## Próximo passo

Beta com utilizadores e o resto do caminho até ao 1.0 em `docs/roadmap.md`.

## Notas

- Os ícones Icons8 do HeidiSQL não podem ser reutilizados, e o nome "HeidiSQL" não deve entrar no nome do produto. O nome desta pasta é só de trabalho.
