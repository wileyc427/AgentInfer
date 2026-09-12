# Security

## Reporting a vulnerability

Report it privately through GitHub: **Security → Advisories → Report a
vulnerability** on this repository. That opens a channel visible only to you
and the maintainer.

Please do not open a public issue for a vulnerability.

Expect an acknowledgement within a week. This is a personal project rather than
a funded one, so there is no paid response window and no bounty — what there is
instead is a maintainer who would rather hear about it than not.

## Scope

Versions below `1.0` are supported only at the current release. There is no
backport branch: fixes go into the next version.

Two things are worth knowing before reporting, because both are design
decisions rather than oversights:

**Tool permissions are advisory unless you supply an authorizer.**
`GrantAllTools` grants everything and is named for what it does so that it is
noticeable in registration code. A deployment that registers it and then reports
that `[RequiresPermission]` did not stop a tool is working as documented.

**This library does not sandbox anything.** It runs your tools, in your process,
with your privileges, when a model asks it to. There is no CodeAct strategy and
no sandbox — the model chooses *which* declared tool to call and with what
arguments, and nothing more. Treat every `[AgentTool]` you declare as reachable
by anyone who can influence the model's input, and gate it accordingly.

What *is* in scope: the permission gate failing open, an argument binding in a
way the declared schema should have rejected, a credential reaching a log or an
exception message, or generated code that does something its attributes do not
say it does.
