// 前端 ESLint 平面配置（配合 frontend-gate.cjs 的架构规则，负责语言质量）
import js from '@eslint/js';
import globals from 'globals';

export default [
  {
    files: ['app/**/*.js'],
    ignores: ['app/vendor/**'],
    languageOptions: {
      ecmaVersion: 2023,
      sourceType: 'module',
      globals: { ...globals.browser },
    },
    rules: {
      ...js.configs.recommended.rules,
      'no-var': 'error',
      'prefer-const': 'warn',
      eqeqeq: ['error', 'smart'],
      'no-unused-vars': ['warn', { argsIgnorePattern: '^_' }],
    },
  },
];
