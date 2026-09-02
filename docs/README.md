# Project documentation

Feature documentation lives in `docs/features/<NNN>-<feature-slug>/` and is maintained with the implementation. Spec Kit commands must use `SPECIFY_FEATURE_DIRECTORY` set to that directory so the repository does not create a parallel `specs/` tree.

Each Spec Kit feature has:

- `spec.md` — scope, functional requirements, acceptance criteria, and non-functional requirements;
- `plan.md` — technical and user-facing design decisions;
- `tasks.md` — the single numbered task list used to execute and track the work;
- `reviews/pre-push-review.md` — the final alternate-model adversarial review and disposition.

Additional durable records live in:

- `docs/architecture/decisions/` for numbered architecture decision records;
- `docs/releases/<version-or-date>/` for coordinated release plans and evidence;
- `docs/incidents/` for material production incident records.

The detailed authoring and delivery rules are in the repository skills under `.agents/skills/`. Specifications should be concise and proportional, but all material decisions must be explicit and traceable from requirement to task to test.

See [official-skills.md](official-skills.md) for the upstream source and scope of vendored OpenAI skills.

See [Spec Kit integration](features/001-spec-kit-integration/spec.md) for the pinned version, Codex-only integration, and upgrade rules.
