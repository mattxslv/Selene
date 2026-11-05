# Ideas status

Use one status label on an Ideas discussion:

- `idea: investigating`: investigating feasibility or a reported behavior; no implementation promise.
- `idea: planned`: the maintainer has approved the work, but it has not shipped.
- `idea: implemented`: the requested behavior has shipped. Partial delivery does not qualify; identify any remaining scope first.
- `idea: waiting`: blocked by an external dependency or a needed reporter response. Keep the reason in the discussion.

Unlabelled ideas are unscheduled. Votes help prioritize; they do not authorize features. Remove the previous status when changing it. Do not auto-close Ideas when code lands: related reports and follow-up questions can remain useful.

The reviewed assignments and label descriptions are tracked in `.github/idea-status.json`. Preview with `python tools/ideas-status.py`; apply with `python tools/ideas-status.py --apply` after reviewing changes. It only changes those assignments, preserves unrelated labels and posts no comments.

Current reviewed requests: #123, #135, #140, #147 and #152 shipped in 1.5.6. #144 and #154 remain under investigation; their exact reporter setups have not been reproduced. #125 is waiting on the listing submission process described in its existing status comment. #121's custom-filter use case is substantially covered by Playnite presets, but it should be discussed before marking the request implemented. The other ideas remain unscheduled.
