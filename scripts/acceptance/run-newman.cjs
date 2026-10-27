// Receive ephemeral credentials over stdin, run the unchanged collection, and emit counts only.
// No private environment export, raw report, request log, or CLI reporter is written.
const fs = require('node:fs');
const path = require('node:path');
const newman = require('newman');
const root = path.resolve(__dirname, '../..');
let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => {
  input += chunk;
  if (input.length > 16384) process.exit(1);
});
process.stdin.on('end', () => {
  try {
    const values = JSON.parse(input);
    const target = new URL(values.baseUrl);
    if (target.protocol !== 'http:' || target.hostname !== '127.0.0.1' || target.username ||
        target.password || target.pathname !== '/' || target.search || target.hash) throw new Error();
    const environment = JSON.parse(fs.readFileSync(path.join(root, 'docs/postman/local.template.postman_environment.json'), 'utf8'));
    for (const item of environment.values) {
      if (typeof values[item.key] !== 'string' || !values[item.key]) throw new Error();
      item.value = values[item.key];
    }
    newman.run({
      collection: path.join(root, 'docs/postman/cms-events.postman_collection.json'),
      environment, reporters: [], silent: true, ignoreRedirects: true,
      timeout: 120000, timeoutRequest: 35000, timeoutScript: 5000
    }, (error, summary) => {
      if (error || !summary) {
        process.stderr.write('Isolated Postman execution failed; private diagnostics were suppressed.\n');
        process.exitCode = 1;
        return;
      }
      process.stdout.write(JSON.stringify({ requests: summary.run.stats.requests.total,
        assertions: summary.run.stats.assertions.total, failures: summary.run.failures.length }) + '\n');
      process.exitCode = summary.run.failures.length ? 1 : 0;
    });
  } catch {
    process.stderr.write('Invalid isolated runner input; no private values were printed.\n');
    process.exitCode = 1;
  }
});
