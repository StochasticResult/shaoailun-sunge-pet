# ShaolanPet 技术文档

只想装上玩的话看 [README](README.md)。这一份写给要自己编译、改代码、或者被派来装这个东西的 agent。

---

## 这是什么

从两期口播里剪出来的 37 句原声，做成一个桌面小挂件。

点头像，他说话。拖着他，换个地方待着。右键能挑句子、固定一句、改轮播内容。

不开摄像头，不开麦克风，不要管理员权限，不联网。退出就是真的退了。

## 为什么默认只开 8 句

37 句全扔进轮播是吵的。这批素材是口播，大半句子是论证过程里的一环 —— 「而是你的意志力」「也不是租来的」这种，脱离上下文就是半句话，接在别的话后面也没头没尾。

所以默认只开这 8 句，它们自己站得住：

| | |
| --- | --- |
| `07` AI 救不了你 | `26` 过度健身吸引到的是同性 |
| `16` 数字不会骗人 | `30` 薄肌的核心逻辑就是要帅 |
| `17` 我不相信你没有空健身 | `31` 我已经是版本之子了 |
| `19` 蛋白粉不是科技 | `32` 中国人必须得吃饭 |

想听别的：右键 →「轮播内容」，把勾打回去。这个选择会记住，不用每次重设。

## 安装

要直接下载安装包，去 [Releases](https://github.com/StochasticResult/shaoailun-sunge-pet/releases/latest) 下最新的 `.dmg`。下面是源码构建。

需要 **Apple Silicon Mac** 和 **macOS 14 或更新版本**。

```sh
git clone https://github.com/StochasticResult/shaoailun-sunge-pet.git
cd shaoailun-sunge-pet
./build.sh          # 编译 + 跑一遍自检，最后一行 PASS 才算过
open build/薄肌桌宠.app
```

装进「应用程序」：

```sh
./install.sh --build
```

不需要 Xcode 工程，`swiftc` 直接编，没有任何第三方依赖。

## Windows 版

Windows 10/11 64 位版使用 .NET 8 和 WPF，一个程序包含薄肌、太对了和峰哥，控制面板可以分别开关桌宠、调整大小和音量或停止全部声音，大小与右键菜单同步。每个角色支持点击播放、拖动、右键选择句子、轮播筛选、固定句子及大小设置。独立运行，不需要安装 .NET；三个角色会在一人说完后自动接话，可在控制面板开关。三份设置分别存放在 `%LOCALAPPDATA%\ShaolanPet\settings.json`、`sunpet.json` 和 `fengge.json`，已有薄肌设置会保留。关闭面板退出全部程序，最小化面板后可通过任务栏恢复，也可从人物右键菜单打开。

在 Windows 上安装 .NET 8 SDK 后构建：

```powershell
dotnet publish Windows/ShaolanPet.Windows.csproj -c Release -r win-x64 --self-contained true -o dist/windows
.\dist\windows\ShaolanPet.exe
```

将整个 `dist/windows` 文件夹一起分发（含 Assets），不要只复制 exe。推送 main 后 [Windows 构建](https://github.com/StochasticResult/shaoailun-sunge-pet/actions/workflows/windows.yml) 会运行自检并提供压缩包；版本标签发布时，Windows 包也会上传到 Release。`--self-test` 检查三套素材、随机轮播、控制面板开关、大小与位置保存及模拟接话流程；`--playback-test` 在 Windows 上静音播放实际音频并验证四句接话顺序。若构建机没有可用声音设备，该播放测试报告跳过；其他播放错误会导致构建失败。自动测试不能代替手动听音和体验。

Windows 自动接话顺序与 Mac 一致：点薄肌后依次是太对了、峰哥、太对了；点峰哥后太对了接一句；点太对了不会再触发接话。只有已开启的角色能接话，接话角色没有可用的轮播句时会停止。控制面板的「自动接话」默认开启，选择会记住；停止播放、关闭角色或再次点击会取消旧的接话。Windows 合集内部直接协调语音播放完成事件，不使用 Mac 的跨进程通知；Windows 每句播放完再接，节奏与 Mac 的尾音重叠略有不同。

### Codex 消息联动（Windows）

控制面板的「Codex 消息联动（所选聊天）」开启时，所选本机 Codex 聊天中可识别的新真人输入会让太对了随机播放一条现有语音。太对了自身也须开启。此入口不启动其他角色的接话链；原有手动播放、音量、位置、大小和自动接话设置仍有效。关闭联动会清空等待队列并停止采集。

在 `%LOCALAPPDATA%\ShaolanPet\codex-reaction-source.json` 中指定聊天 URL 末尾的 ID：

```json
{"threadId":"<selected-chat-guid>"}
```

也可增加 `rolloutPath`，指向该聊天位于 `CODEX_HOME/sessions` 内的确切本机记录。修改配置后关闭并重新开启联动。缺少有效配置时，原桌宠仍可使用，来源状态会说明监听未就绪。不要把个人配置、聊天记录或 ID 提交到仓库。

这是轻量、只读的本地兼容桥，不是回答完成通知，不调用模型，也不修改 Codex。它只使用所选记录中的结构化消息种类、来源和回合信息，不保存或外发消息正文。当前核验的来源是 Codex Desktop 的 `0.165.0-alpha.1.2` 记录格式；该内部格式不是稳定公开 API。来源、版本或字段不明时拒绝触发，不能将「正在监听」等同于已经验证联动。

启动、文件替换和截断均从当前文件末尾设水位，不播放旧消息。启动前已开始的回合或自动化回合中的输入可能被保守跳过。内部派单、心跳、自动化、工具与 assistant 消息不触发。最多等待 16 条反应，超过队列上限会丢弃新增事件；手动播放优先。去重为进程内有界最佳努力，不保证跨进程恰好一次。用户排除到仅剩一条语音时，无法保证连续不同。

`--version` 提供构建版本；`--dump-state` 是一次性配置探测，不是正在运行实例的状态。`--codex-playback-test` 在隔离设置下静音验证排队播放；它不能代替真人发送消息到实际听音的验收。采集器的独立 Windows 测试可通过 `dotnet run --project Tests/CodexMessageWatcher/CodexMessageWatcher.Tests.csproj` 执行。

## 用法

| 操作 | 效果 |
| --- | --- |
| 点人物 | 随机说一句，一轮内不重复 |
| 播放中再点 | 掐掉上一句，说下一句，不叠音 |
| 拖动 | 换位置，退出后还记住 |
| 右键人物 / 点菜单栏「薄肌」 | 打开设置 |

右键菜单里：

- **播放方式**：随机一句 / 固定播一句。固定模式下每次点他都是同一句，适合钉住某句反复听
- **挑一句**：直接播指定那句。固定模式下点哪句，就把哪句钉住
- **轮播内容**：每句一个勾。点勾不会关菜单，可以连着改好几句
- **音量 / 人物大小 / 移回屏幕右下角 / 退出**

## 三个桌宠

第四个是 [果冻桌宠](https://github.com/aurelius358/JellyPet)：形状挂件，没有语音，也不在 `Bridge.swift` 这套协议里，所以不接话。

这一组桌宠共用一套联动协议，同时跑起来会自己接话：

```
薄肌 ──> 太对了 ──> 峰哥 ──> 太对了
```

| 桌宠 | 说什么 | 在联动里的位置 |
| --- | --- | --- |
| [好事桌宠](https://github.com/Ganzhe2028/FengGe-Desktop-Pet) | 峰哥 · 11 句「这是个好事」 | 接太对了的话，但只在薄肌起的链里 |
| [太对了 · SunPet](https://github.com/Ganzhe2028/SunPet) | 孙宇晨 · 28 句「太对了」 | 谁说话都接 |
| **薄肌桌宠**（本仓库） | 邵艾伦 · 37 句 | 起头，自己不接 |

点薄肌起头会一路接下去：太对了 → 峰哥 → 太对了，四句一串，到第四句停。点峰哥只有太对了接一句就停。点太对了没人接。

峰哥和薄肌之间没有直接通道。太对了接完峰哥链就断了，不会无限往下接 —— 靠通知里带的跳数封顶，不靠谁记得「别接」。

节奏上默认提前 0.3 秒叠进来。第二跳（太对了 → 峰哥）压到 0.2 秒，四步连起来才不拖。另外每句开头垫的静音长短差很多，启动时逐句量出来，起播时刻各自补偿 —— 不然同样提前 0.3 秒，实际出声能飘出小半秒。

实现上走 `DistributedNotificationCenter`，同一个用户会话里跨进程直达，实测 5ms。不开端口、不联网、不要任何权限。对方在不在跑用 bundle id 查 `NSRunningApplication`，不靠「发一条看有没有人回」来猜。协议在 `Sources/Bridge.swift`，三个仓库各放一份完全相同的副本，改一处要三处同步。

只装一个也能用，联动是可选的。

## 素材来源与声明

- **音频**：B 站 [为什么我劝所有男人练薄肌？](https://www.bilibili.com/video/BV1xa4k6vEpp/)（17:20）和 [如何练出薄肌？训练、饮食、减脂一次讲透](https://www.bilibili.com/video/BV1httT6LEfy/)（18:22）里逐句截取，用原声，没有变声或语音克隆。每句的时间码写在 `Assets/clips.json` 的 `source` 字段，可以逐条核对
- **头像**：视频封面，用 macOS Vision 的原生抠图做了去背景，补成正方形透明 PNG
- 音频和头像的版权属于原权利人，**不在 MIT 许可范围内**。这个仓库只做学习交流和玩梗，不商用。权利人如果觉得不合适，开个 issue，会移除
- 代码用 MIT

## 技术细节

- 切句用的是 `ffmpeg` 解码全片 + numpy 按采样点裁。**不要用 `ffmpeg -ss/-to` 组合**，位置会偏且不报错
- 转写用 faster-whisper（small / int8），两期共出 1650 段，人工挑出 37 句
- 每句切完会去头尾静音、RMS 归一化到 -20 dBFS、峰值压到 0.95 以下，所以 37 句响度一致
- `clips.json` 里每句有两个标记：`answer` 表示能不能当捧哏（接话句是「对别人的话做反应」，观点句自成一派），`defaultOff` 表示首次启动是否排除出轮播

## License

代码 MIT，见 [LICENSE](LICENSE)。音频、头像等素材版权归原权利人所有。

## 加第四个人

三个仓库是同一份代码的复制品，加角色就是复制这个模式：

1. `Sources/Bridge.swift` 的 `PetRole` 枚举加一个 case，值是新角色的 bundle id，`displayName` 里补中文名。
2. `peers` 表决定「我听谁」。现在三份是各自硬编码的：想让它接谁的话，就把谁加进去；只说不接的角色 peers 留空。
3. `Bridge.swift` 三个仓库逐字节相同（md5 一致），改一处就要三处同步，否则联动会半通。
4. 新仓库直接复制任意一个，换掉 `Assets/` 里的音频、`clips.json`、`face.png` 和 bundle id，`./build.sh --package` 出安装包。
