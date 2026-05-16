// scripts/build-info.js
const { execSync } = require("child_process");
const fs = require("fs");

function run(cmd) {
  try {
    return execSync(cmd).toString().trim();
  } catch {
    return "unknown";
  }
}

function safe(val) {
  return `"${val.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}

const content = `
# AUTO GENERATED (BUILD INFO)
GIT_COMMIT=${run("git rev-parse HEAD")}
GIT_AUTHOR=${safe(run("git log -1 --pretty=format:%an"))}
GIT_BRANCH=${run("git rev-parse --abbrev-ref HEAD")}
GIT_MESSAGE=${safe(run("git log -1 --pretty=format:%s"))}
BUILD_TIME=${new Date().toISOString()}
`;

console.log(`
Generated build info:
${content}`);

fs.appendFileSync(".env", content);
console.log(`✅ appended build info to .env`);
