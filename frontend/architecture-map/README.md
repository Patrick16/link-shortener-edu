# Architecture Map

An interactive educational diagram of the project's architecture: clicking a node/connection opens
a details card (stack, config, links to code); a scenario switcher shows scenarios 1-5 (from the
minimal stack up to pgcat sharding and pooler scaling).

Data schema: see `src/types/architecture.ts` and the example in `src/data/architecture.json`.

Stack: React + TypeScript + Vite.

## Development

```bash
npm install
npm run dev
```
