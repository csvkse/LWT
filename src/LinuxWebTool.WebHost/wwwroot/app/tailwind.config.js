// Tailwind（本地 vendor/tailwind.js）配置：系统字体栈，内网零外网依赖
window.tailwind = window.tailwind || {};
window.tailwind.config = {
  theme: {
    extend: {
      colors: {
        'cyber-bg': '#070b14',
        'cyber-panel': '#0d1526',
        'cyber-panel-2': '#121d36',
        'cyber-line': '#1d2c4a',
        'neon': '#22d3ee',
        'neon-soft': '#67e8f9',
      },
      fontFamily: {
        display: ['"Segoe UI"', 'Inter', 'system-ui', 'sans-serif'],
        sans: ['"Segoe UI"', '"PingFang SC"', '"Microsoft YaHei"', 'system-ui', 'sans-serif'],
        mono: ['"JetBrains Mono"', 'Consolas', '"Courier New"', 'monospace'],
      },
    },
  },
};
