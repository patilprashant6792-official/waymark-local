# Prompts and skills

Waymark gives Claude your code and your computer. The files here tell Claude how to work with them like a careful senior engineer. They are optional, plain Markdown, and yours to edit.

| Path | What it is | Where it goes |
|---|---|---|
| [expert-programmer.md](expert-programmer.md) | The main prompt. Engineering judgment that is not tied to a language: what to build, what will break, what "done" means. It ends with a short Waymark section. | Your Claude preferences or `CLAUDE.md` |
| [skills/effort-mirror](skills/effort-mirror/SKILL.md) | Skill. Sizes a task, lists every job a senior would do (research, options, build, tests, fresh-eyes review), and does all of them in one run with evidence. | Claude skills |
| [skills/prompt-smith](skills/prompt-smith/SKILL.md) | Skill. The standard for writing prompts and skills: shorter, no lost rules. Use it when you edit the files in this folder. | Claude skills |

The main prompt works on its own. It uses `effort-mirror` only when that skill is installed.

## Use the main prompt

Copy the whole of [expert-programmer.md](expert-programmer.md) and paste it where Claude reads your standing instructions:

- **claude.ai:** Settings > Profile, in your personal preferences. It applies to new chats only.
- **Claude Code:** `~/.claude/CLAUDE.md` for every project, or `CLAUDE.md` in a repository for that project only.
- **Waymark:** the **Host prompt** page ("Instructions from the owner") also works and reaches every connected agent, but Claude reads it only after it has loaded the Waymark overview. See [docs/guides.md](../docs/guides.md). Personal preferences are read first, so use them for the Waymark section below.

### Keep the Waymark section

The last section, **Tools**, is what makes Claude reach for Waymark. If you trim the rest, keep it:

- **Your computer.** Questions about this machine (monitors, hardware, OS settings, installed apps, processes, files) go to Waymark's `cmd_shell`. Without that line claude.ai can answer "this chat isn't linked to your computer" before it ever looks at Waymark, because it checks its own desktop-app link first.
- **Package signatures.** Waymark's `pkg_*` tools show what a package really exposes at the version your project pins, so Claude does not web search or guess names and parameters. Web search is only for how to use a package.
- **Start with `waymark_guide`**, and verify changes with Waymark's build and test commands.

## Install the skills

Both skills are folders with a `SKILL.md`. `prompt-smith` also has a `references` folder that must stay with it.

**claude.ai and the Claude desktop app**

1. Zip the skill folder (`effort-mirror` or `prompt-smith`). The ZIP must contain the folder itself, not only the files inside it. In File Explorer, right-click the folder and choose **Compress to ZIP file**.
2. Go to **Customize > Skills**, choose **+**, then **Create skill > Upload a skill**, and upload the ZIP.
3. Turn the skill on. Skills need code execution enabled (Settings > Capabilities on Free, Pro and Max plans). See [Claude's guide to skills](https://support.claude.com/en/articles/12512180).

**Claude Code**

Copy the folders into your personal skills folder (every project) or into `.claude/skills/` in one repository (that project only):

```powershell
New-Item -ItemType Directory -Force "$HOME\.claude\skills" | Out-Null
Copy-Item -Recurse prompts\skills\effort-mirror, prompts\skills\prompt-smith "$HOME\.claude\skills\"
```

See the [Claude Code skills documentation](https://code.claude.com/docs/en/skills).

## Change them

The copies in this folder are the source of truth. Edit them here, then paste the main prompt again or upload the skill ZIP again. Ask Claude to use `prompt-smith` when you rewrite a prompt, so it keeps every rule while getting shorter. Do not put secrets, personal paths or machine names in these files; the Host prompt page refuses secrets because every connected agent receives that text.
