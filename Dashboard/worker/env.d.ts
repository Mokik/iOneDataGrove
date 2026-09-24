// D1 is optional: the local dashboard uses PostgreSQL through the ASP.NET API.
// The starter's getDb() checks for this binding before using D1.
declare namespace Cloudflare {
  interface Env {
    DB?: D1Database;
  }
}
