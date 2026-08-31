# Official upstream skills

This repository vendors two skills from the official [`openai/skills`](https://github.com/openai/skills) curated catalog so the workflow is available to every repository user:

- [`gh-address-comments`](https://github.com/openai/skills/tree/main/skills/.curated/gh-address-comments) — inspect and address GitHub pull-request review comments;
- [`gh-fix-ci`](https://github.com/openai/skills/tree/main/skills/.curated/gh-fix-ci) — inspect and diagnose failing GitHub Actions pull-request checks.

They were downloaded from upstream `main` at commit `49f948faa9258a0c61caceaf225e179651397431` on 2026-08-31. Their included `LICENSE.txt` files are preserved. Review upstream changes before updating the vendored copies.

`gh-address-comments` carries a repository-local patch on top of that upstream snapshot. The patch resolves fork-based PRs through the base-repository PR URL, adds a clear error when `gh` is missing, fixes malformed UI metadata, and includes regression tests. Recheck whether upstream has incorporated these fixes before the next vendor update.

These are Codex skills maintained in OpenAI's catalog. GitHub Skills at `skills.github.com` are interactive learning courses and are not interchangeable with Codex `SKILL.md` packages.
