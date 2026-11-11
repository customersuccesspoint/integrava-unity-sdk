/**
 * Local mint server for the Unity chat sample.
 *
 * The Integrava secret key stays in this process. The app asks this route
 * for a short-lived identify token and never sees the key.
 *
 * This sample trusts externalUserId in the request body. It listens on
 * 127.0.0.1 only. A production route must mint for the player your login
 * already authenticated, and must not be left open on the internet.
 *
 *   INTEGRAVA_SECRET_KEY=integrava_sk_... node mint-identify-token.mjs
 *
 * Optional:
 *   INTEGRAVA_API_BASE   default https://api.integrava.ai
 *   PORT                 default 8787
 *   INTEGRAVA_MINT_PASSWORD  if set, Unity must send the same value as
 *                            X-Integrava-Mint-Password. This is your password,
 *                            not the Integrava secret key.
 */
import { timingSafeEqual } from "node:crypto";
import http from "node:http";

const secret = process.env.INTEGRAVA_SECRET_KEY || "";
const apiBase = (process.env.INTEGRAVA_API_BASE || "https://api.integrava.ai").replace(/\/$/, "");
const port = Number(process.env.PORT || 8787);
const mintPassword = process.env.INTEGRAVA_MINT_PASSWORD || "";

if (!secret.startsWith("integrava_sk_") && !secret.startsWith("opsflow_sk_")) {
  console.error("Set INTEGRAVA_SECRET_KEY to your server key. Do not put that key in the Unity client.");
  process.exit(1);
}

function passwordMatches(header) {
  if (!mintPassword) return true;
  const got = Buffer.from(String(header || ""));
  const expected = Buffer.from(mintPassword);
  if (got.length !== expected.length) return false;
  return timingSafeEqual(got, expected);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on("data", (chunk) => {
      size += chunk.length;
      if (size > 16 * 1024) {
        reject(new Error("body too large"));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

const server = http.createServer(async (req, res) => {
  if (req.method === "OPTIONS") {
    res.writeHead(204, {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Headers": "content-type, x-integrava-mint-password",
      "Access-Control-Allow-Methods": "POST, OPTIONS"
    });
    res.end();
    return;
  }
  if (req.method !== "POST" || req.url !== "/identify-token") {
    res.writeHead(404, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "Not found" }));
    return;
  }
  if (!passwordMatches(req.headers["x-integrava-mint-password"])) {
    res.writeHead(401, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "Mint password required" }));
    return;
  }
  let raw = "";
  try {
    raw = await readBody(req);
  } catch {
    res.writeHead(413, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "Body too large" }));
    return;
  }
  let payload = {};
  try {
    payload = JSON.parse(raw || "{}");
  } catch {
    res.writeHead(400, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "Invalid JSON" }));
    return;
  }
  const externalUserId = String(payload.externalUserId || "").trim();
  if (!externalUserId) {
    res.writeHead(400, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ error: "externalUserId is required" }));
    return;
  }
  const upstream = await fetch(`${apiBase}/api/v1/identify-token`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${secret}`,
      "Content-Type": "application/json"
    },
    body: JSON.stringify({
      externalUserId,
      displayLabel: payload.displayLabel,
      traits: payload.traits,
      widgetId: payload.widgetId
    })
  });
  const text = await upstream.text();
  res.writeHead(upstream.status, {
    "Content-Type": "application/json",
    "Access-Control-Allow-Origin": "*"
  });
  res.end(text);
});

server.listen(port, "127.0.0.1", () => {
  console.log(`Identify mint listening on http://127.0.0.1:${port}/identify-token`);
});
