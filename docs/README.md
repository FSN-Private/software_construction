# Project documentation

Feature documentation lives in `docs/features/<feature-slug>/` and is maintained with the implementation.

Each feature has:

- `requirements.md` — scope, functional requirements, and acceptance criteria;
- `design.md` — technical and user-facing design decisions;
- `non-functional.md` — measurable quality, security, accessibility, compatibility, and operational expectations;
- `implementation-plan.md` — the single numbered task list used to execute and track the work;
- `reviews/pre-push-review.md` — the final alternate-model adversarial review and disposition.

Additional durable records live in:

- `docs/architecture/decisions/` for numbered architecture decision records;
- `docs/releases/<version-or-date>/` for coordinated release plans and evidence;
- `docs/incidents/` for material production incident records.

The detailed authoring and delivery rules are in the repository skills under `.agents/skills/`. Specifications should be concise and proportional, but all material decisions must be explicit and traceable from requirement to task to test.

See [official-skills.md](official-skills.md) for the upstream source and scope of vendored OpenAI skills.
