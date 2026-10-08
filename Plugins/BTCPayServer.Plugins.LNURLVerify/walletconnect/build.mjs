import { build } from 'esbuild'

await build({
  entryPoints: ['src/wallet.js'],
  bundle: true,
  format: 'iife',
  globalName: 'LnurlVerifyWallet',
  minify: true,
  platform: 'browser',
  target: 'es2020',
  inject: ['src/shims.js'],
  define: { global: 'globalThis' },
  outfile: '../Resources/lnurlverify/wallet.js'
})
