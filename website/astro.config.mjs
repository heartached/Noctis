// @ts-check
import { defineConfig } from 'astro/config';

// https://astro.build/config
export default defineConfig({
  site: 'https://noctisapp.cc',
  output: 'static',
  trailingSlash: 'ignore',
  build: {
    inlineStylesheets: 'auto',
  },
  compressHTML: true,
  devToolbar: { enabled: false },
  vite: {
    // The plugin directory reads ../plugins/index.json and the icons beside
    // it, which sit outside website/. Builds can always reach them; the dev
    // server needs leave to.
    server: { fs: { allow: ['..'] } },
  },
});
