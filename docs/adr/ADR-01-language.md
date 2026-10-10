# ADR-01: Linguagem

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/specification.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Equipa for C++ forte

## Opções

C#, C++, Rust, Go, JVM, TypeScript

## Decisão

C# / .NET 10 LTS

## Razão

Drivers, segurança de memória, AOT, velocidade de desenvolvimento (secção 18)

## Trade-offs

Memória base maior que C++

## Risco

GC em resultados grandes

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
