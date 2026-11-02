# StudioMCP 0.2.0 RC2

- Adds native folders, reparenting and character attach-point discovery: 63 tools total.
- Attach props to either hand, head or waist using native Studio points. Parenting keeps world placement; subsequent transforms are parent-local.
- Scene snapshots report character parents, attach points and world positions. Names and relationships use native scene PNG persistence.

Update the host and ONE matching game bridge, then refresh the client's tool list. KKPE is not required for hierarchy tools.

KK live checks passed for nested folders, batch parenting, FK hand following, detach, invalid/stale requests and scene reload; an actual Screencap was also inspected.
The branch's earlier KKS hierarchy regression is documented in the verification history. This does not qualify the full KKPE/IK/Timeline suite on KK.
