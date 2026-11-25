# MDL 头部内部名 · 只读验证报告

**性质**：只读实验。未修改生产代码、规则表、缓存、分类树、用户数据；未改动任何 MOD 文件。
**样本**：U 盘 `D:\游戏内容\求生之路2各项MOD` 下 110 个 vpk 里的全部 177 个 `.mdl`。
**读取量**：每个模型最多读 262 144 字节（内部名在第 12–75 字节，所需仅 80 字节）。

---

## 0. 结论

1. **技术可行**：177 个模型里 172 个成功读出内部名（97.2%），172/172 的头部长度字段与目录表条目长度完全一致，可作为免费完整性校验。
2. **14 个条目名 ≠ 内部名**（8.1%），与我上一轮的说法一致；其中 `tiepen.mdl`、`wmcapple.mdl` 的条目名确实不含任何对象信息。
3. **但包级判定 0 个改变**：14 个案例所在的包里，都还带着一个正确命名的官方模型（如 `v_shotgun_chrome.mdl`、`w_eq_medkit.mdl`），现有路径法早就靠它判对了。内部名只是重复确认。
4. **所以上一轮那句「MDL 内部名是关键」说重了**：它是解释层和去重层的材料，不是识别覆盖面的扩充。这一点必须按实测改正。

---

## 1. 方法与约束

| 项 | 内容 |
|---|---|
| 读取 | 只读打开 vpk，取目录表定位 `.mdl`，读该条目开头若干字节 |
| 解析 | magic `IDST`、version、checksum、name[64]、header length、纹理表偏移 |
| 不解析 | 骨骼、动画、网格、VVD/VTX 内容 |
| 不执行 | 没有任何脚本或代码执行 |
| 对照 | 用 `Dict` 的匹配规则（脚本复刻）分别对「条目名」和「内部名」求对象；仅覆盖模型名这一路证据 |

---

## 2. 技术结果

| 指标 | 数值 |
|---|---|
| MDL 总数 | 177 |
| 成功解析 | 172（97.2%） |
| 失败 | 5 |
| 版本分布 | 49 × 172 |
| 名称字段为空 / 含非 ASCII | 0 / 0 |
| 头部长度 == 条目大小 | 172 / 172 |
| 头部长度范围 | 2 216 B ~ 2 241 804 B（均值 280 631 B） |

5 个失败项（全部安全降级，没有异常退出）：

| 文件 | 原因 |
|---|---|
| `models/weapons/arms/v_arms_gambler_new.mdl` | version 53，非 L4D2 标准 49 |
| `models/survivors/survivor_gambler.mdl` | version 53 |
| `models/v_models/v_m60.mdl`（mg42 包） | version 53 |
| `models/props_unique/spawn_apartment/coffeeammo.mdl` | 数据段读到的字节 < 80（分卷缺失或偏移越界） |
| `models/props/terror/ammo_stack.mdl` | 同上 |

---

## 3. 14 个不一致案例（逐项）

「包级路径结论」= 只看包内其他模型的条目名，按现有 v_ 优先规则能得到什么。

| # | 条目名 | 内部名 | 包内其他模型 | 包级路径结论 | 内部名结论 | 改变判定 |
|---|---|---|---|---|---|---|
| 1 | v_huntingrifle1 | v_huntingrifle | w_sniper_mini14, v_huntingrifle | 15发木狙 | 15发木狙 | 否 |
| 2 | v_huntingriflefix | v_huntingrifle | w_sniper_mini14, v_huntingrifle | 15发木狙 | 15发木狙 | 否 |
| 3 | v_huntingrifle1 | v_huntingrifle | w_sniper_mini14, v_huntingrifle | 15发木狙 | 15发木狙 | 否 |
| 4 | v_sniper_military1 | v_sniper_military | w_sniper_military, v_sniper_military | 30发军用狙 | 30发军用狙 | 否 |
| 5 | v_rifle_ak47_1 | v_rifle_ak47 | w_rifle_ak47, v_rifle_ak47 | AK-47 | AK-47 | 否 |
| 6 | v_rifle_ak471 | v_rifle_ak47 | w_rifle_ak47, v_rifle_ak47 | AK-47 | AK-47 | 否 |
| 7 | v_rifle_ak471 | v_rifle_ak47 | w_rifle_ak47, v_rifle_ak47 | AK-47 | AK-47 | 否 |
| 8 | v_rifle1 | v_rifle | w_rifle_m16a2, v_rifle | M16 | M16 | 否 |
| 9 | v_desert_rifle-1 | v_desert_rifle | w_desert_rifle, v_desert_rifle | SCAR-L | SCAR-L | 否 |
| 10 | v_rif_sg5521 | v_rif_sg552 | w_rifle_sg552, v_rif_sg552 | sg552 | sg552 | 否 |
| 11 | v_smg1 | v_smg | w_smg_uzi, v_smg | UZI | UZI | 否 |
| 12 | **wmcapple** | w_eq_medkit | w_eq_medkit, v_medkit, madebyrem | 医疗包 | 医疗包 | 否 |
| 13 | v_pumpshotgun1 | v_pumpshotgun | w_shotgun, v_pumpshotgun | {铁喷, 木喷} → 取木喷 | 木喷 | 否 |
| 14 | **tiepen** | v_shotgun_chrome | w_pumpshotgun_a, v_shotgun_chrome | {木喷, 铁喷} → 取铁喷 | 铁喷 | 否 |

第 12、14 行是仅有的两个「条目名单独看等于没有信息」的模型。但：

- `tiepen` 所在的包里同时有 `v_shotgun_chrome.mdl`；
- `wmcapple` 所在的包里同时有 `w_eq_medkit.mdl` 和 `v_medkit.mdl`。

所以路径法并没有被这两个名字挡住——包里那份「正经名字」的模型先被判掉了。

---

## 4. 一致性统计

| 指标 | 数值 |
|---|---|
| 条目名 == 内部名（忽略大小写） | 158 |
| 不一致 | 14（8.1%） |
| 内部名指向**另一个**对象（冲突） | 0 |
| 模型级「内部名独有」 | 2（tiepen、wmcapple） |
| **包级「内部名独有」** | **0** |
| 含模型的包 | 69，全部在路径层已有结论 |

---

## 5. 可识别性

| 类别 | 数量 | 例子 |
|---|---|---|
| 内部名可映射到位置表 | 154/172 | v_m60 → M60、v_shotgun_chrome → 铁喷 |
| 内部名映射不到 | 18 | `w_sniper_mini14`、`madebyrem`、`ksep`、`gestures_*`、`boomette`、`v_snip_scout` |
| 内部名指向错误对象 | 0 | — |

映射不到的 18 个不是 MDL 的问题，是词表缺这些名字（自定义模型名、表情骨架、非标准特感名）。

---

## 6. 泛化价值评估

- **对「包内唯一模型被改名」有效**：若一个包只有一个 `tiepen.mdl`，路径法归零，内部名能直接给「铁喷」。
- **但这类包在 110 个包里是 0 个**：所有改名模型旁边都躺着一份官方命名的同款。作者的常见做法是「复制官方模型 + 另存改件」，官方那份仍在。
- 因此不能说「内部名提升识别率」。它能提升的是：解释（为什么这个包是铁喷）、去重（两个条目名指向同一内部名，是同款复制）、完整性（头部长度与条目长度比对）。

**收益判断**：在当前语料上，识别覆盖面增益 0；可解释性与去重收益真实但小。

---

## 7. 安全与成本

| 项 | 结论 |
|---|---|
| 读取量 | 每模型 80 字节起（实测只读头部即可判断，纹理表可选） |
| 校验 | magic + 版本白名单(44/48/49) + 长度上限 + 名称可打印性，四道都不通过就返回空 |
| 异常 | 5 个失败项全部返回空，未抛异常、未影响其他条目 |
| 执行风险 | 无；纯字节解析，不加载模型 |
| 实现成本 | 约 40 行 C#；单包额外 < 0.1 ms |

---

## 8. 建议

1. **不单独为「内部名识别」立原型**：本语料证明它不扩大识别面，独立立项的投入产出不成立。
2. **并入 C1（MDL 头）作为字段**，只做两件事：写进 Trace（解释），以及同名去重（同包多个条目指向同一内部名 → 标为同款复制）。
3. **不写 Subject**：在「包级唯一模型被改名」的样本出现之前，内部名不应单独决定对象。
4. **重新评估的触发条件**：遇到只含改名模型的包，或改件类 MOD 明显增多时，再拿新样本复核一次。

---

## 附：数据文件

- `_probe/mdl_census.py` / `_probe/mdl.jsonl`：177 个模型的逐条记录（路径、内部名、版本、校验和、头部长度、是否等长）
- `_probe/mdl_an.py` / `mdl_an.txt`：一致性与冲突统计
- `_probe/pkg_cmp.py` / `pkg_cmp.txt`：包级对照
- `_probe/cases.py` / `cases.txt`：14 个案例的上下文

全部位于仓库外，只读，可丢弃；未写入 `D:\游戏内容` 任何位置。
