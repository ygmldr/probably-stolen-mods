# 仓库历史重写记录（2026-11-17）

> **旧的 commit SHA 已全部失效。** 本文给出 旧 → 新 的完整映射。

## 1. 为什么重写

推送远程前做泄露扫描，发现两类内容不应出现在远程历史里：

1. **凭据**：`PawnMind/docs/plans/SERVER_MIGRATION_2026-11-17.md` 与
   `PawnMind/docs/PAWNMIND_HANDOFF_2026-11-17.md` 曾写入服务器密码与邮箱明文。
   工作树先做过清理，但历史里仍然存在。
2. **第三方反编译源码**：`CompactSort/analysis/sia_decompiled/`（SmartInventoryArrange，213 个文件）
   是别人的 Mod 反编译产物；即便上游是 MIT，也不应随本仓库分发。

## 2. 做了什么

```bash
git filter-repo --invert-paths --path CompactSort/analysis/sia_decompiled
git filter-repo --replace-text replacements.txt   # 密码 / 邮箱 -> <redacted>
```

- 反编译源码从**全部历史提交**移除，工作树同步删除；本地副本保留在 `_decomp/sia/`（已 gitignore）。
- `CompactSort/analysis/competitor_bench/CompetitorBench.csproj` 改为通过 `$(SiaSourceRoot)` 引用，
  默认 `_decomp/sia`；缺源码时该项目不参与构建。
- 1 个提交在替换后与父提交内容完全相同（`chore(docs): redact host credentials`），被自动剪除。
- 验证：`git log --all -S` 无命中；`git grep` 遍历全部提交无命中；
  `git log --all --name-only` 中 `sia_decompiled` 计数 0。

## 3. 旧 → 新 映射

| 旧 SHA | 新 SHA | 主题 |
|---|---|---|
| `73972d0d41` | `20d469867c` | Initial commit: CompactSort, PawnMind MCP bridge and research docs |
| `acfae3f1b9` | `49f285d393` | chore(docs): move PawnMind docs under PawnMind/docs |
| `48461892bf` | `641c33cf09` | chore(pawnmind): ignore host-local .env.server |
| `8aef00a120` | `6e75c0d2a3` | chore(pawnmind): manifest 0.3.0 with current capabilities |
| `299ecd489d` | `2ae409c5a5` | docs(agent): first-week skill, offline benchmark spec and trace validator (Nightly E) |
| `7385a43152` | `0430d6f185` | docs(audit): player entry matrix for 58 write ops + 4 impure reads |
| `f789a92f7b` | `9f3c7b4f0e` | docs(audit): static mod compatibility matrix and workshop install draft (Nightly G) |
| `ea3f1c603b` | `6b5df7d20b` | docs(memory): P3 reflection design and prompt templates |
| `f7ae62c117` | `65fd5a3b1a` | docs(nightly): final reports, baseline, test matrix and schema sync |
| `531ff052ad` | `aec43dcca3` | docs(review): independent module and integration reviews (Nightly F) |
| `f96a339c56` | `5a0bed3d8b` | docs: P6-P10 capability matrix and limitations; full regression |
| `74eb4803c6` | `367b6bd4f0` | docs: P6/P7 feature summary and supplier order result |
| `0a1f4a90ac` | `dbfa18843d` | docs: capability matrix and live sell verification |
| `21acfa95cb` | `fd761f2481` | docs: complete nightly handoff document |
| `75db83a996` | `8da85347e0` | docs: designate save slot 17 as the reusable test save |
| `03d27f1f59` | `186ed4d642` | docs: expand systems plan with fish marking pen, phone branches and all backlog systems |
| `27aef97f85` | `f87dadecc6` | docs: expert brief on native-guard parity |
| `cac34ba892` | `d3ac6a11d9` | docs: final P6-P10 matrix with security and contraband |
| `5f228f10b0` | `6204d7f44d` | docs: final verification scope and v0.5.0 |
| `fd287da9b6` | `6494e856bc` | docs: game agent long-term memory architecture spec v1 |
| `5c0fedbbdd` | `0d5a38054a` | docs: mark module A review as NOT_REVIEWED in handoff |
| `1439dd74c0` | `e0c02348dd` | docs: memory system handoff for incoming engineer |
| `c91db408a0` | `83fb71ea1e` | docs: nightly baseline, test matrix and server handoff |
| `9335be36f5` | `fa9a5ed0d9` | docs: normal-gameplay plan, known limitations and iteration issue log |
| `87b04bf20f` | `7037669ed4` | docs: note repack fallback in PawnMind README |
| `7ca05bfb3c` | `5bc38cdbdc` | docs: note repack normal-path regression check |
| `8bdf393d6d` | `bd11c2963d` | docs: refresh capability audit counts after screenshot feature |
| `5725f303d6` | `14488017db` | docs: reorganize README and AGENTS for external review |
| `ab700043a6` | `86ca916e06` | docs: review the player-parity RFC against the real 0.46D code |
| `b841a6ad85` | `eab9110331` | docs: round-2 disposition, ADR v2 and generated tool policy |
| `236844c95d` | `0c53812698` | docs: round-3 batch-1 report |
| `3369a31181` | `5578dd9b9f` | docs: server-side agent bootstrap and continuation guide |
| `723fe3550f` | `357af7cf3e` | docs: systems expansion plan (analyzers, tools, labels, phone, lottery, machines) |
| `2bfdc30053` | `975173de69` | feat(agent): declarative tool policy with deny-by-default and evidence gates |
| `8af0116c90` | `671311d957` | feat(compact-sort): 1.9.4 all-container drag fit and overflow guards |
| `f76dd50204` | `190b1812a9` | feat(memory): P2 memory store in its own database |
| `d0440ab8eb` | `298be3aa51` | feat(memory): P2 memory tools, retrieval evaluation and RFC input |
| `239c27735d` | `b31dbe1ad4` | feat(pawnmind): P0 remediation and P1 reliable interaction history |
| `9c9ae485d1` | `3f5b19b545` | feat(pawnmind): close_windows for paper notes, books and floating windows |
| `3c274b8a94` | `3152d7c0d9` | feat(pawnmind): day report, calendar, clientele and faction perks |
| `054983531a` | `99fddf8048` | feat(pawnmind): dismiss_current_client escape hatch |
| `6b13ab2abf` | `749c312112` | feat(pawnmind): make native confirmation modals visible and answerable |
| `12eb67064d` | `ae2ef423f0` | feat(pawnmind): offline S0-S7 recording pipeline, DSH host and preflight |
| `f09314e3f5` | `598325b660` | feat(pawnmind): operation two-axis state and trade outcome protocol (Nightly D+C) |
| `8f641660f1` | `c47caa518c` | feat(pawnmind): screenshot evidence capture and debug bundle |
| `450ff9644d` | `bb787ed1be` | feat(pawnmind): widen recording surface to the full player toolset |
| `5ddaf78b2a` | `df66314b79` | feat: 180/270 rotation for exact item placement |
| `6de3377265` | `233abfe4a6` | feat: 2D backpack layout, shape masks, cell placement and debug hotkey |
| `acf533cf7e` | `edea1ba5ed` | feat: analyzer results, editable labels and marking pen modes |
| `5d56d96c36` | `bdcfb9497b` | feat: bargain preview with odds and reputation estimate |
| `b861f9ad5d` | `7c6ae3aa11` | feat: commissary/jackson/wholesale goods scopes and commissary buy |
| `3eb3154676` | `a4ef95661d` | feat: current dialogue text, wanted flags and report any customer |
| `61b07578bf` | `994cef830c` | feat: expedition unlock, dispatch and advance actions |
| `0738f67a4d` | `ac10487091` | feat: expedition, event and sign status snapshots |
| `7a89fcafd2` | `850c8eaa66` | feat: generic item interactions (activate/toggle/target) and live slot-17 check |
| `718a723b6d` | `e2cd5c59bf` | feat: goods catalog list_goods(client) and move success verification |
| `bb58d0263c` | `08f0e078e4` | feat: husbandry snapshot and safe expedition abort |
| `ff85715669` | `fde66d1d81` | feat: hydroponic full cycle and wine status/fermentation API |
| `fd2d00b951` | `1bdd56e33c` | feat: hydroponic grow status and slot setup |
| `19f5ac2acb` | `4226b85c00` | feat: internal repack fallback with rollback for fragmented containers |
| `fe2e8e9366` | `a188395963` | feat: long-poll events endpoint and wait_for_events MCP tool |
| `fe53381e36` | `fd45de5530` | feat: lottery status, ticket inspection and redeem |
| `c4509ba30f` | `1f4769a2a0` | feat: machine listing, slot inspection and insert/collect scaffolding |
| `ebdc25683a` | `e1cbf86934` | feat: machine-slot item lookup and wine rack insertion |
| `f7e39ba58e` | `5a7beb53d6` | feat: network upgrade purchase and supplier phone introduction |
| `dc92f8b691` | `3a12224433` | feat: night locations, travel, scavenge and return |
| `c46621e5e7` | `d798fc3e39` | feat: owned book list, vanilla/paper/multipage reading and read_item |
| `58b2abbaa0` | `89eaabe416` | feat: phone contacts, call, dialogue choices and hangup |
| `71274257fc` | `452b1fba9c` | feat: request delivery, barter accept and wanted arrest actions |
| `ef20383ca3` | `e43fe47b56` | feat: wholesale purchase action and wholesale math |
| `79cff289a4` | `e6c030799b` | feat: wine water/ingredient helpers and full fermentation cycle |
| `f68f60ea0b` | `b6bfee4516` | fix(agent): classify read tools from the recorder allowlist |
| `9c7f5ba3fc` | `e277592703` | fix(dsh): restore the missing --config flag in the MCP patch |
| `3b4b1cd700` | `45c680567a` | fix(memory): stop flagging our own writes as external state changes |
| `70e9d0d332` | `9895c3b4ed` | fix(pawnmind): D0a snapshot purity, mode policy gate and R3 remediation |
| `9bf54229e6` | `86e883c56b` | fix(pawnmind): advance_dialogue must not silently no-op |
| `a0b4021a7b` | `49bcd1a4be` | fix(pawnmind): allow --memory on when launched by script path |
| `3d85963359` | `0ea8e26387` | fix(pawnmind): close the P0 guard gaps found by the native audit |
| `a54b1edf74` | `c36f9c3920` | fix(pawnmind): close_windows false mismatch and long-poll error shape |
| `62a183a949` | `9c409a143c` | fix(pawnmind): confirm_trade presses the native accept button |
| `95c30fc696` | `35b514dfae` | fix(pawnmind): declare reflection-external capability in manifest |
| `7548ddb23a` | `0c193e17ac` | fix(pawnmind): fail-closed policy fingerprint and review findings |
| `63ae0eff2f` | `9bcdc564f5` | fix(pawnmind): fail-closed run mode and endpoint gate (Nightly A) |
| `6ab33339f9` | `b4c613c8f8` | fix(pawnmind): pure snapshot contract and legal-action candidates (Nightly B) |
| `3fed61cb15` | `e65d631d0f` | fix(pawnmind): ring the bell through its native guards |
| `a23b708565` | `3d330a34ee` | fix(pawnmind): stop sleep from skipping the landlord; drop manual save |
| `afc72fcc17` | `9675992b7d` | fix: filter wholesale goods and expose wholesale offer in state |
| `d3591dbdc4` | `77fde9110f` | test: chemical scanner live on dream dust and caffeine pill |
| `a7872ebe8c` | `8e6da8e964` | test: furnace battery/scrap insertion and machine placement finding |
| `8b2a6dbc1e` | `443ff08d83` | test: furnace full cycle (insert, day-end processing, collect) |
| `c232fa5966` | `2940c52a71` | test: gated debug item grants and live fish marking |
| `1d5993721c` | `81a7a02fd2` | test: live repack success on fragmented showcase |
| `b03dcba61d` | `da7bbb6766` | test: three-day run regression and capability matrix update |

## 4. 已有克隆

```bash
git clone <url> game_mods        # 最干净
# 或在现有克隆里：确认没有本地未推送提交后
git fetch origin && git reset --hard origin/main
```

## 5. 仍然有效的约定

- 仓库保持 **private**（文档里仍有内部路径/局域网地址）。
- 新文档不写凭据；反编译产物只放 `_decomp/`，不进仓库。
