# ADR-18: Motor dos filtros regex

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/specification.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Suíte de 40 casos falhar

## Opções

Regex .NET backtracking, NonBacktracking, porta do TRegExpr

## Decisão

Regex .NET (IgnoreCase, CultureInvariant) com timeout de 50 ms

## Razão

Suporta lookahead como o TRegExpr; o modo NonBacktracking não suporta

## Trade-offs

Pequenas diferenças de sintaxe (ex.: `\d` Unicode em .NET)

## Risco

Divergência em casos raros

## Implementação (spike da Fase 1)

`Engine.Sql/Filters/TreeFilterExpression.cs`, com `RegexOptions.IgnoreCase | CultureInvariant` e timeout de 50 ms. Uma regex inválida e um timeout deixam o nó visível. Testada com 49 casos de regex derivados da 4.3 (`tests/Unit/Engine.Sql.Tests/TreeFilterExpressionTests.cs`) e com as regras por tipo de nó (`tests/Unit/UI.Controls.Tree.Tests`). Os casos ainda não foram validados em runtime no HeidiSQL (Fase 0).

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
