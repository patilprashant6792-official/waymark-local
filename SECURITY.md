# Security policy

Waymark gives an AI assistant real access to your computer: what it reads can reach your Claude chat, and what it runs acts as you. Security reports are taken seriously. Please read [docs/safety.md](docs/safety.md) first; it explains what Waymark limits and, as important, what it does not.

## Reporting a vulnerability

Please do not open a public issue for a security problem.

Use GitHub's private reporting instead: open the **Security** tab of this repository and choose **Report a vulnerability**. Include:

- what you found and why it matters,
- the steps to reproduce it (a short script or a list of clicks),
- the version or commit you tested, and your Windows version.

This is a one-person open-source project, so replies are best effort and there is no service-level promise. You will get an answer as soon as it can be read. Please give a reasonable time to fix the problem before you share it publicly.

## Supported versions

Only the latest commit on `main` is supported. Fixes are not back-ported.

## What counts as a vulnerability

Examples that are in scope:

- a way around the approval step, or around the Off / Ask / Auto setting of an operation;
- a way to read, write or run something outside the allowed workspaces or the access rules;
- a secret that should have been hidden in a tool result but was sent in the clear;
- a way to approve a request from outside the local admin pages;
- a flaw in sign-in (OAuth), access keys, or the way the tunnel is exposed;
- anything that lets another person on the network or the internet use Waymark without being let in.

## What does not count

- An assistant doing what you approved, or what you set to Auto. Auto means no question is asked.
- Prompt injection in general: text in a file or web page can steer an assistant. Waymark limits the damage but cannot remove this risk. A way around a limit that Waymark claims to enforce is in scope.
- Problems that need an attacker who already controls your Windows account.
- Secrets whose shape Waymark cannot recognise; the scanner finds known formats, not every secret.
- Missing hardening on `localhost` pages that only the signed-in Windows user can reach.

## For people who run Waymark

- Keep the tools you do not need switched off, and leave risky operations on **Ask**.
- Read what an approval shows before you press Approve.
- Treat the data folder (`%LOCALAPPDATA%\Waymark`) and the code index in `waymark.db` like source code: they describe your projects.
- If a secret was shown to an assistant, rotate it and delete the chat.
