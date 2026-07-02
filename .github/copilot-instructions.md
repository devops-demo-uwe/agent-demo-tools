# Copilot instructions for this repository

This repository is a teaching demo for Azure Foundry Agents tool calling with a C# 10 console app.

When changing this project:

- Keep the code small, readable, and suitable for a live teaching session.
- Prefer explicit control flow over clever abstractions so learners can follow the tool-calling loop.
- Preserve the main teaching point: the LLM requests a function call, but the C# host application validates arguments, runs the function, and submits the result.
- Use C# 10-compatible syntax. Do not introduce collection expressions, raw string literals, required members, or other newer language features.
- Add comments when they explain the tool-calling concept, SDK lifecycle, or security boundary.
- Do not store Azure endpoints, keys, tokens, or other secrets in source control.
- Keep README instructions accurate after code changes, especially package names, environment variables, and run commands.
- Validate changes with `dotnet build` before considering the task complete.