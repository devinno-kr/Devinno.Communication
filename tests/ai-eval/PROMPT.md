# Prompt template for the evaluated agent

Replace `<docs-path>`, `<map-name>` and `<MAP-CONTENT>`, then give the text below (and nothing else) to a fresh AI agent whose only file access is `<docs-path>`.

`<docs-path>` is a copy of the `docs/` folder from the NuGet package (`llms.txt`, `llm-rules.md`, `comm-xml-reference.md`, `schemas/`) plus, optionally, `Devinno.Communication.xml` (the IntelliSense file from `lib/net8.0/`). It must **not** contain anything from `tests/ai-eval/` or the repository source.

---

You are a C# developer. You have a memory map of an industrial controller and must write the communication configuration and a small console program for it, using the Devinno.Communication .NET 8 library.

You only have the folder `<docs-path>` (copied from the library's NuGet package). Start by reading `llms.txt` in that folder, then follow it. Do not look anywhere else: no web search, no other folders, no library source code. If something is not in that folder, it does not exist; do not invent elements, attributes, methods or enum values.

Task: below is a memory map of an industrial controller. Produce two files:

- `<map-name>.xml`: the communication configuration for this library that fits the map.
- `<map-name>.cs`: a small single-file C# console app that connects to the controller, logs every received value, and writes each writable signal once, using the library's recommended way. It must compile against the library.

Check your work against the documents before you finish. Answer with exactly these two files.

Memory map:

<MAP-CONTENT>
