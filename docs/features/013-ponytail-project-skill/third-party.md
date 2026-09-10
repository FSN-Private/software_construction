# Third-Party Notice: Ponytail v4.8.4

## Source and review

- **Project**: Ponytail — lazy senior developer mode
- **Authoritative source**: https://github.com/DietrichGebert/ponytail
- **Release**: `v4.8.4`
- **Immutable source commit**: `bc9ee949d5f439e8b9f3bb92c6d6d3d1e6ebd324`
- **Reviewed**: 2026-09-03
- **Included upstream content**: the six `skills/*/SKILL.md` files listed below
- **Excluded upstream content**: lifecycle hooks, JavaScript, plugin manifests, package manifests, lockfiles, install scripts, tests, images, and all host adapters

The release tag was fetched from the authoritative repository and verified to resolve to the immutable commit above. The selected files are Markdown instruction content with YAML front matter; no runtime package or transitive dependency is added.

## Included files and SHA-256

| Upstream path | Project path | SHA-256 |
|---|---|---|
| `skills/ponytail/SKILL.md` | `.agents/skills/ponytail/SKILL.md` | `e575026e2a6dffe408b10e6f7acc7ed47c83d71fc69f9c0d3880115f3f6c6aa7` |
| `skills/ponytail-audit/SKILL.md` | `.agents/skills/ponytail-audit/SKILL.md` | `49e835c96313009179ec162d80ccc9d91625e387c8af7cd3975c531ba105b2d5` |
| `skills/ponytail-debt/SKILL.md` | `.agents/skills/ponytail-debt/SKILL.md` | `c0a5c0c0cc843cb53f15b792790946768500b45538d07fadbf0602ed6af63a47` |
| `skills/ponytail-gain/SKILL.md` | `.agents/skills/ponytail-gain/SKILL.md` | `f8847c410b54978fff41eab775fc754d41ede19ad6fb72fac799b65616cc6683` |
| `skills/ponytail-help/SKILL.md` | `.agents/skills/ponytail-help/SKILL.md` | `e801f17fe963cb92d50d015df43932150eaecf2511183b20659dc72a70cf6c97` |
| `skills/ponytail-review/SKILL.md` | `.agents/skills/ponytail-review/SKILL.md` | `6c8b7e5c897a406b66da6aabda7fa6509e8d1d447e73e602265d7986497445d1` |

## License

Ponytail is distributed under the MIT License:

```text
MIT License

Copyright (c) 2026 DietrichGebert

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Update and removal

To update, select a new immutable upstream release, verify its tag-to-commit identity, review the six-file diff and license, regenerate these SHA-256 values, run [quickstart.md](quickstart.md), and commit the change separately. Do not track the mutable `main` branch.

To remove Ponytail, delete the six `.agents/skills/ponytail*/SKILL.md` files and this notice. No runtime or user-global state was created by this project-scoped integration.
