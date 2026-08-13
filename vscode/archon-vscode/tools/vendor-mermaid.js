// Copies Mermaid's browser bundle into media/, so the call-trace webview can load it as a local
// resource without a CDN — VS Code webviews block remote script sources by default CSP.
//
// Run with `node tools/vendor-mermaid.js`. media/ is build output, not checked in (see .gitignore),
// so this has to run before packaging, same as publish-host and compile.

const fs = require('fs');
const path = require('path');

const source = path.join(__dirname, '..', 'node_modules', 'mermaid', 'dist', 'mermaid.min.js');
const targetDir = path.join(__dirname, '..', 'media');
const target = path.join(targetDir, 'mermaid.min.js');

if (!fs.existsSync(source)) {
    console.error(`Could not find ${source} — run 'npm install' first.`);
    process.exit(1);
}

fs.mkdirSync(targetDir, { recursive: true });
fs.copyFileSync(source, target);
console.log(`Wrote ${target}`);
