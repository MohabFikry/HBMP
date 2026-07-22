// Conventional Commits enforcement (CLAUDE.md § Commit convention).
// types: feat, fix, chore, refactor, test, docs, perf, sec.
// Scope = service/app (e.g., audit, patient, web). Reference story id (US-xxx) + phase in body.
module.exports = {
  extends: ['@commitlint/config-conventional'],
  rules: {
    'type-enum': [
      2,
      'always',
      ['feat', 'fix', 'chore', 'refactor', 'test', 'docs', 'perf', 'sec'],
    ],
    'subject-case': [0],
    'header-max-length': [2, 'always', 100],
  },
};
