import { spawn } from "node:child_process";

const packageRunner = process.env.npm_execpath;
if (!packageRunner) {
  throw new Error("Impossibile individuare il gestore dei pacchetti.");
}

const commonOptions = { cwd: process.cwd(), stdio: "inherit", windowsHide: true };
const api = spawn("dotnet", ["run", "--project", "./server/iOneDataGrove.Dashboard.Api.csproj", "--no-launch-profile"], commonOptions);
const web = spawn(process.execPath, [packageRunner, "run", "web"], commonOptions);

let stopping = false;
function stop(exitCode = 0) {
  if (stopping) return;
  stopping = true;
  api.kill();
  web.kill();
  process.exitCode = exitCode;
}

api.on("error", (error) => {
  console.error("Impossibile avviare il servizio dati:", error.message);
  stop(1);
});

web.on("error", (error) => {
  console.error("Impossibile avviare il sito:", error.message);
  stop(1);
});

api.on("exit", (code) => {
  if (!stopping && code !== 0) stop(code ?? 1);
});

web.on("exit", (code) => {
  if (!stopping) stop(code ?? 0);
});

process.on("SIGINT", () => stop());
process.on("SIGTERM", () => stop());
