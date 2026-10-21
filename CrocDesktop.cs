using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Runtime.InteropServices;

// Native per-pixel-alpha window: no browser frame, background or taskbar button.
class CrocDesktop : Form {
    [StructLayout(LayoutKind.Sequential)] struct PointNative { public int X,Y; public PointNative(int x,int y){X=x;Y=y;} }
    [StructLayout(LayoutKind.Sequential)] struct SizeNative { public int X,Y; public SizeNative(int x,int y){X=x;Y=y;} }
    [StructLayout(LayoutKind.Sequential,Pack=1)] struct Blend { public byte Op,Flags,Alpha,Format; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr dst,ref PointNative pos,ref SizeNative size,IntPtr src,ref PointNative source,int color,ref Blend blend,int flags);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr w,IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfoHeader { public int Size,Width,Height; public short Planes,BitCount; public int Compression,SizeImage,XPels,YPels,ClrUsed,ClrImportant; }
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfoHeader info,uint usage,out IntPtr bits,IntPtr section,uint offset);
    readonly string root, runtime;
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly Timer timer=new Timer();
    readonly ContextMenuStrip contextMenu=new ContextMenuStrip();
    // Frame strips built by BuildCrocSprites.py from the original drawings.
    Image rest,work,poses,bottle,bottleEmpty;
    // Sprites pre-resampled once per scale, so each frame copies small bitmaps.
    Bitmap[] restFrames=new Bitmap[0],workFrames=new Bitmap[0],poseFrames=new Bitmap[0];
    // poses.png order, matching BuildCrocSprites.POSES.
    const int Crouch=0, Recoil=1, Relief=2, Think=3, Drink=4, Jump=5;
    Bitmap bottleScaled,bottleEmptyScaled,poopScaled;
    double spriteScale=0;
    readonly string[] barText=new string[4];
    readonly Font[] barFont=new Font[4];
    // The frame is drawn straight into a premultiplied DIB that stays selected
    // into its own DC, so each update hands the same pixels to the compositor.
    Bitmap frame;
    IntPtr surfaceDc=IntPtr.Zero,surfaceBitmap=IntPtr.Zero,surfaceOld=IntPtr.Zero;
    NotifyIcon tray;
    Dictionary<string,object> state=new Dictionary<string,object>();
    List<Particle> particles=new List<Particle>();
    List<Drop> drops=new List<Drop>();
    List<Puff> puffs=new List<Puff>();
    string lastRun="", status="正在连接本地日志";
    double scale=1, demoUntil=0, demoSeconds=2, demoQuota=100, laneChangeAt=0;
    readonly double[] laneEmission={0,.33,.66};
    float motion=0, smoothSpacing=50;
    int bottles=0, lanes=0;
    int pendingLanes=0;
    bool working=false,emitting=false,dragging=false,loaded=false,visibleScene=true,wasWorking=false;
    DateTime previous=DateTime.UtcNow, lastRead=DateTime.MinValue;
    // Animation state, all advanced by Animate(dt) on the scene clock.
    double clock=0, poseSwitchAt=-10, lastRender=-1, nextBlink=1.8, blinkStart=-10, nextDrop=0, nextPick=3, pickStart=-10, lastPuff=-1;
    // Short-lived key poses: recoil after a single shot, relief after a stream, drinking when quota drops.
    double recoilPoseUntil=-1, reliefUntil=-1, drinkUntil=-1;
    bool doubleBlink=false, landed=true, wasEmitting=false, restShown=true;
    float lastQuota=-1;
    string lastQuotaWindow="";
    int pickLoops=1;
    // Springs: recoil (px, negative toward the snout), tail sweep (degrees),
    // the bottles' lagging follow of the head (px) and their own tilt.
    float recoil=0, recoilVel=0, tail=0, tailVel=0, follow=0, followVel=0, hopY=0;
    readonly float[] bottleTilt=new float[5], bottleTiltVel=new float[5];
    float squash=0, squashVel=0, lean=0, effort=0, toolsBlend=0, quotaPercent=0, fillShown=1;
    readonly float[] bottleScale=new float[5], bottleVel=new float[5];
    readonly Random random=new Random();
    class Particle { public float X,Y,TargetY,Speed,Age,Hop; public int Index; }
    class Drop { public float X,Y,VX,VY,Age; }
    class Puff { public float X,Y,Age,Size; }
    // Scene geometry in the 420 x 244 logical canvas.
    // Every frame shares one square canvas whose feet point (59%, 95%) sits on PivotX, PivotY.
    const float SpriteX=84.43f, SpriteY=-11.86f, SpriteSize=208, PivotX=207, PivotY=185;
    const float ButtX=238, ButtY=138, EndX=408, PoopW=18, PoopH=15;
    static readonly float[] LaneY={138,114,162}; // Butt height first, then fanned above and below.
    // Eye whites as fractions of the drawing (left, top, right, bottom).
    static readonly float[,] RestEyes={{.3536f,.2536f,.4964f,.3911f},{.5411f,.2643f,.6839f,.3982f}};
        // Tail sweep of each work.png frame, matching BuildCrocSprites.TAIL.
    static readonly float[] TailAngles={-8,-4,0,4,8,12,16,20};
    static readonly Color Outline=Color.FromArgb(84,104,36), LidTop=Color.FromArgb(206,236,140), LidBottom=Color.FromArgb(160,190,96);
    static double Now(){return (DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds;}
    string Value(string key,string fallback=""){object v;return state.TryGetValue(key,out v)&&v!=null?v.ToString():fallback;}
    double Number(string key,double fallback=0){double n;return double.TryParse(Value(key),out n)?n:fallback;}
    bool Flag(string key){return Value(key)=="True";}
    protected override CreateParams CreateParams { get {var p=base.CreateParams;p.ExStyle|=0x80000|0x80;return p;} }
    protected override bool ShowWithoutActivation {get{return true;}}
    public CrocDesktop(string project,string data,bool demo,bool smoke){
        root=project;runtime=data;Directory.CreateDirectory(runtime);
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;
        Text="Croc Token 桌面鳄鱼";
        string art=Path.Combine(root,"web/assets/desktop");
        rest=Image.FromFile(Path.Combine(art,"rest.png"));work=Image.FromFile(Path.Combine(art,"work.png"));poses=Image.FromFile(Path.Combine(art,"poses.png"));
        bottle=Image.FromFile(Path.Combine(art,"bottle.png"));bottleEmpty=Image.FromFile(Path.Combine(art,"bottle-empty.png"));
        var area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-450,area.Bottom-230);
        try{var saved=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(runtime,"layout.json")));scale=Convert.ToDouble(saved["scale"]);Location=new Point(Convert.ToInt32(saved["x"]),Convert.ToInt32(saved["y"]));}catch{}
        scale=Math.Max(.7,Math.Min(2,scale));ClientSize=new Size((int)(420*scale),(int)(244*scale));
        bool onScreen=false;foreach(var screen in Screen.AllScreens)if(screen.WorkingArea.IntersectsWith(Bounds))onScreen=true;
        if(!onScreen)Location=new Point(area.Right-Width-20,area.Bottom-Height-20);
        tray=new NotifyIcon();tray.Icon=SystemIcons.Application;tray.Text="桌面鳄鱼 · 右键设置";tray.Visible=true;
        tray.MouseClick+=(s,e)=>{if(e.Button==MouseButtons.Right)Popup().Show(Cursor.Position);else if(e.Button==MouseButtons.Left){visibleScene=true;Show();Render(true);}};
        MouseDown+=(s,e)=>{if(e.Button==MouseButtons.Left){dragging=true;ReleaseCapture();SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);dragging=false;Save();}else if(e.Button==MouseButtons.Right)Popup().Show(Cursor.Position);};
        MouseWheel+=(s,e)=>ResizeScene(scale+(e.Delta>0?.1:-.1));
        timer.Interval=33;timer.Tick+=(s,e)=>Tick(); // ~30 fps; all motion is time-based.
        Shown+=(s,e)=>{loaded=true;if(demo)Demo(smoke?.7:2,smoke?(double?)100:null);timer.Start();Render(true);};
        if(smoke){var check=new Timer();check.Interval=1000;check.Tick+=(s,e)=>{
            check.Stop();timer.Stop();
            try{
                if(clock<.3)throw new Exception("Animation timer is not running");
                if(!IsWindowVisible(Handle)||frame==null||!working||bottles!=5||lanes!=3)throw new Exception("Working window state invalid");
                if(frame.GetPixel(0,0).A!=0)throw new Exception("Background is not transparent");
                if(frame.GetPixel((int)(90*scale),(int)(216*scale)).A<200)throw new Exception("Bottom data bar missing");
                if(BottleCount(0)!=0||BottleCount(20)!=1||BottleCount(74)!=4||BottleCount(100)!=5)throw new Exception("Quota bottle mapping invalid");
                state["quota_status"]="recorded";state["quota_remaining"]=54;state["quota_resets_at"]=Now()+3600;state["rate"]=42;state["cache_percent"]=80;
                var bar=BarValues();if(bar.Length!=4||bar[0]!="54%"||bar[2]!="42 tok/s"||bar[3]!="80%")throw new Exception("Data bar values invalid");
                Render();frame.Save(Path.Combine(runtime,"working-preview.png"),ImageFormat.Png);
                working=false;emitting=false;Render();
                if(working)throw new Exception("End did not restore resting pose");
                frame.Save(Path.Combine(runtime,"resting-preview.png"),ImageFormat.Png);
                Demo(4);Tick();if(lanes!=1)throw new Exception("Slow throughput must use one lane");
                Demo(2);Tick();if(lanes!=2)throw new Exception("Medium throughput must use two lanes");
                Demo(.8);Tick();if(lanes!=3)throw new Exception("Fast throughput must use three lanes");
                Demo(2,40);Tick();if(bottles!=2)throw new Exception("Quota demo invalid");
                foreach(double t in new[]{4.0,3.0,2.0,1.2,.7}){Demo(t);Tick();if(bottles!=2||lanes!=DemoLanes(t))throw new Exception("Animation changed bottles or tier invalid");}
                state["model"]="smoke-model";state["cache_percent"]=80;
                for(int i=0;i<8;i++){var testMenu=Popup();testMenu.Show(PointToScreen(new Point(30,30)));Application.DoEvents();testMenu.Close();Application.DoEvents();if(testMenu.IsDisposed)throw new Exception("Context menu disposed while handling messages");}
                if(!contextMenu.Items[2].Text.Contains("smoke-model")||!contextMenu.Items[3].Text.Contains("80"))throw new Exception("Model/cache menu fields missing");
                state["quota_windows"]=new object[]{
                    new Dictionary<string,object>{{"quota_window_minutes",300},{"quota_window_label","5 小时"},{"quota_status","recorded"},{"quota_remaining",54}},
                    new Dictionary<string,object>{{"quota_window_minutes",10080},{"quota_window_label","1 周"},{"quota_status","recorded"},{"quota_remaining",30}}};
                var periodMenu=QuotaMenu();
                if(periodMenu.DropDownItems.Count!=3)throw new Exception("Quota period menu missing windows");
                ((ToolStripMenuItem)periodMenu.DropDownItems[2]).PerformClick();
                var preference=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(runtime,"quota-selection.json")));
                if(Convert.ToInt32(preference["window_minutes"])!=10080)throw new Exception("Quota period selection was not saved");
                ((ToolStripMenuItem)periodMenu.DropDownItems[0]).PerformClick();periodMenu.Dispose();
                state["threads"]=new object[]{new Dictionary<string,object>{{"id","smoke-chat"},{"title","Test chat"}}};
                state["selected"]="smoke-chat";
                Choose("smoke-chat");
                using(var pinnedMenu=ThreadMenu()){
                    if(((ToolStripMenuItem)pinnedMenu.DropDownItems[0]).Checked||!((ToolStripMenuItem)pinnedMenu.DropDownItems[3]).Checked)throw new Exception("Pinned menu check invalid");
                    ((ToolStripMenuItem)pinnedMenu.DropDownItems[0]).PerformClick();
                }
                for(int i=0;i<3;i++)using(var autoMenu=ThreadMenu()){
                    if(!((ToolStripMenuItem)autoMenu.DropDownItems[0]).Checked||((ToolStripMenuItem)autoMenu.DropDownItems[1]).Checked||((ToolStripMenuItem)autoMenu.DropDownItems[3]).Checked)throw new Exception("Auto-follow check did not persist");
                }
                Choose("","current");
                using(var currentMenu=ThreadMenu()){
                    if(((ToolStripMenuItem)currentMenu.DropDownItems[0]).Checked||!((ToolStripMenuItem)currentMenu.DropDownItems[1]).Checked||((ToolStripMenuItem)currentMenu.DropDownItems[3]).Checked)throw new Exception("Current-chat follow check invalid");
                }
                Choose("");
                File.WriteAllText(Path.Combine(runtime,"smoke-result.json"),json.Serialize(new{visible=true,transparent=true,working=true,restored=true,menuCycles=8,modelAndCache=true,animationTimer=true,fiveSpeedTiers=true,bottleCountPreserved=true,bottomDataBar=true,cacheInDataBar=true,quotaPeriodMenu=true,autoFollowChecked=true,currentChatMode=true,poopMotion=true,width=Width,height=Height}));
            }catch(Exception error){File.WriteAllText(Path.Combine(runtime,"smoke-result.json"),json.Serialize(new{error=error.ToString()}));}
            Close();check.Dispose();};Shown+=(s,e)=>check.Start();}
        FormClosed+=(s,e)=>{timer.Stop();Save();tray.Dispose();contextMenu.Dispose();rest.Dispose();work.Dispose();poses.Dispose();bottle.Dispose();bottleEmpty.Dispose();DisposeSprites();foreach(var f in barFont)if(f!=null)f.Dispose();FreeSurface();};
    }
    void Save(){try{File.WriteAllText(Path.Combine(runtime,"layout.json"),json.Serialize(new{ x=Left,y=Top,scale=scale}));}catch{}}
    void ResizeScene(double value){scale=Math.Max(.7,Math.Min(2,value));ClientSize=new Size((int)(420*scale),(int)(244*scale));Save();Render(true);}
    static Bitmap Resample(Image source,float width,float height,double factor){return Resample(source,new Rectangle(0,0,source.Width,source.Height),width,height,factor);}
    static Bitmap Resample(Image source,Rectangle part,float width,float height,double factor){
        var result=new Bitmap(Math.Max(1,(int)Math.Ceiling(width*factor)),Math.Max(1,(int)Math.Ceiling(height*factor)),PixelFormat.Format32bppPArgb);
        using(var g=Graphics.FromImage(result))
        using(var edges=new ImageAttributes()){
            g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
            edges.SetWrapMode(WrapMode.TileFlipXY); // No bleed from the neighbouring strip frame.
            g.DrawImage(source,new Rectangle(0,0,result.Width,result.Height),part.X,part.Y,part.Width,part.Height,GraphicsUnit.Pixel,edges);
        }
        return result;
    }
    // A strip of square frames, side by side.
    static Bitmap[] Frames(Image strip,double factor){
        int count=Math.Max(1,strip.Width/strip.Height);var frames=new Bitmap[count];
        for(int i=0;i<count;i++)frames[i]=Resample(strip,new Rectangle(i*strip.Height,0,strip.Height,strip.Height),SpriteSize,SpriteSize,factor);
        return frames;
    }
    // Soft-serve swirl drawn as vectors at 4x and resampled once, with the same
    // thick dark outline as the crocodile.
    static Bitmap BuildPoop(double factor){
        const int over=4;float w=PoopW*(float)factor*over,h=PoopH*(float)factor*over;
        using(var big=new Bitmap((int)Math.Ceiling(w),(int)Math.Ceiling(h),PixelFormat.Format32bppPArgb)){
            using(var g=Graphics.FromImage(big)){
                g.SmoothingMode=SmoothingMode.AntiAlias;g.ScaleTransform(w/36f,h/30f);
                var ink=Color.FromArgb(96,60,28);
                float[,] tiers={{2.5f,16.5f,31,12},{6.5f,9.5f,23,10.5f},{10.5f,4,15,8.5f}};
                for(int i=0;i<3;i++){
                    float x=tiers[i,0],y=tiers[i,1],tw=tiers[i,2],th=tiers[i,3];
                    using(var outline=new SolidBrush(ink))g.FillEllipse(outline,x-1.8f,y-1.8f,tw+3.6f,th+3.6f);
                    using(var fill=new LinearGradientBrush(new RectangleF(x,y-1,tw,th+2),Color.FromArgb(206,146,74),Color.FromArgb(160,100,44),90f))g.FillEllipse(fill,x,y,tw,th);
                    using(var shine=new Pen(Color.FromArgb(170,246,214,160),1.6f)){shine.StartCap=shine.EndCap=LineCap.Round;g.DrawArc(shine,x+2.2f,y+1.6f,tw*.55f,th*.7f,200,70);}
                }
                // The little curl on top.
                using(var tip=new GraphicsPath()){
                    tip.AddBezier(15.5f,5.5f,15,1.5f,19,-.2f,22.5f,1.2f);
                    using(var outline=new Pen(ink,4.6f)){outline.StartCap=outline.EndCap=LineCap.Round;g.DrawPath(outline,tip);}
                    using(var fill=new Pen(Color.FromArgb(200,140,70),1.6f)){fill.StartCap=fill.EndCap=LineCap.Round;g.DrawPath(fill,tip);}
                }
            }
            return Resample(big,PoopW,PoopH,factor);
        }
    }
    void DisposeSprites(){
        foreach(var b in restFrames)b.Dispose();foreach(var b in workFrames)b.Dispose();foreach(var b in poseFrames)b.Dispose();
        foreach(var b in new[]{bottleScaled,bottleEmptyScaled,poopScaled})if(b!=null)b.Dispose();
    }
    void EnsureSprites(){
        if(spriteScale==scale)return;
        DisposeSprites();spriteScale=scale;
        restFrames=Frames(rest,scale);workFrames=Frames(work,scale);poseFrames=Frames(poses,scale);
        bottleScaled=Resample(bottle,20,30,scale);bottleEmptyScaled=Resample(bottleEmpty,20,30,scale);
        poopScaled=BuildPoop(scale);
    }
    void FreeSurface(){
        if(frame!=null){frame.Dispose();frame=null;}
        if(surfaceDc!=IntPtr.Zero){SelectObject(surfaceDc,surfaceOld);DeleteObject(surfaceBitmap);DeleteDC(surfaceDc);surfaceDc=IntPtr.Zero;}
    }
    void EnsureSurface(){
        if(frame!=null&&frame.Width==Width&&frame.Height==Height)return;
        FreeSurface();
        IntPtr screen=GetDC(IntPtr.Zero),bits;
        try{
            var header=new BitmapInfoHeader{Size=40,Width=Width,Height=-Height,Planes=1,BitCount=32};
            surfaceDc=CreateCompatibleDC(screen);surfaceBitmap=CreateDIBSection(screen,ref header,0,out bits,IntPtr.Zero,0);
            surfaceOld=SelectObject(surfaceDc,surfaceBitmap);
        }finally{ReleaseDC(IntPtr.Zero,screen);}
        frame=new Bitmap(Width,Height,Width*4,PixelFormat.Format32bppPArgb,bits);
    }
    static int BottleCount(double percent){return (int)Math.Ceiling(Math.Max(0,Math.Min(100,percent))/20);}
    static int DemoLanes(double seconds){return seconds>=3?1:seconds>=1.2?2:3;}
    void Demo(double seconds,double? quota=null){particles.Clear();demoSeconds=seconds;demoQuota=quota??(demoUntil>Now()?demoQuota:bottles*20);demoUntil=Now()+12;working=true;emitting=true;bottles=BottleCount(demoQuota);lanes=DemoLanes(seconds);motion=0;status="模拟演示 · 12 秒后恢复真实数据";}
    string Clock(double seconds){try{return new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime().ToString("HH:mm");}catch{return "未知";}}
    bool QuotaKnown(){return Value("quota_status")=="recorded"&&Number("quota_resets_at")>Now();}
    string ResetTime(bool full){
        double seconds=Number("quota_resets_at");if(seconds<=0)return "—";
        try{var date=new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
            return date.ToString(full?"yyyy-MM-dd HH:mm":date.Date==DateTime.Now.Date?"HH:mm":"MM/dd");
        }catch{return "—";}
    }
    void ChooseQuota(int minutes){
        string path=Path.Combine(runtime,"quota-selection.json"),temporary=path+".tmp";
        File.WriteAllText(temporary,json.Serialize(new{window_minutes=minutes}));
        if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        demoUntil=0;lastRead=DateTime.MinValue;
    }
    ToolStripMenuItem QuotaMenu(){
        var menu=new ToolStripMenuItem("额度周期："+Value("quota_window_label","额度"));
        int selected=(int)Number("quota_selection_minutes");
        var automatic=new ToolStripMenuItem("自动选择（优先 5 小时）");automatic.Checked=selected==0;
        automatic.Click+=(s,e)=>ChooseQuota(0);menu.DropDownItems.Add(automatic);
        object rows;
        if(state.TryGetValue("quota_windows",out rows)&&rows is IEnumerable){
            foreach(var row in (IEnumerable)rows){
                var item=row as Dictionary<string,object>;if(item==null)continue;
                int minutes=Convert.ToInt32(item["quota_window_minutes"]);
                string label=item["quota_window_label"].ToString();
                bool recorded=item["quota_status"].ToString()=="recorded";
                var choice=new ToolStripMenuItem(label+(recorded?" · 剩余 "+Convert.ToDouble(item["quota_remaining"]).ToString("0.#")+"%":" · 等待新记录"));
                choice.Checked=minutes==selected;choice.Click+=(s,e)=>ChooseQuota(minutes);
                menu.DropDownItems.Add(choice);
            }
        }
        if(menu.DropDownItems.Count==1)menu.DropDownItems.Add("暂无本地额度周期记录").Enabled=false;
        return menu;
    }
    ContextMenuStrip Popup(){
        // WinForms may still access the dropdown after Closed; keep it alive
        // until the owner window closes instead of disposing in that event.
        var menu=contextMenu;if(menu.Visible)return menu;
        while(menu.Items.Count>0){var oldItem=menu.Items[0];menu.Items.RemoveAt(0);oldItem.Dispose();}
        menu.Items.Add(status).Enabled=false;
        menu.Items.Add(Value("following","最新活动")+(Value("selected").Length>0?" · "+Value("selected").Substring(Math.Max(0,Value("selected").Length-8)):"")).Enabled=false;
        menu.Items.Add("模型："+Value("model","暂无记录")).Enabled=false;
        menu.Items.Add("最近响应缓存命中："+(Value("cache_percent").Length==0?"暂无计数":Number("cache_percent").ToString("0.#")+"%" )).Enabled=false;
        menu.Items.Add("最近响应输入 / 输出："+Value("last_input","—")+" / "+Value("last_output","—")+" Token").Enabled=false;
        menu.Items.Add("其中缓存输入："+Value("cached_input","—")+" Token").Enabled=false;
        menu.Items.Add(Value("quota_window_label","额度")+"剩余："+(QuotaKnown()?Number("quota_remaining").ToString("0.#")+"%":"未知 / 等待更新")).Enabled=false;
        menu.Items.Add("额度重置："+ResetTime(true)+"（本机时间）").Enabled=false;
        menu.Items.Add("本地额度记录："+(Number("quota_recorded_at")>0?Clock(Number("quota_recorded_at")/1000):"暂无")).Enabled=false;
        menu.Items.Add("输出吞吐："+(Value("rate").Length>0?Number("rate").ToString("0.#")+" tok/s（最近响应平均）":"等待计数")).Enabled=false;
        menu.Items.Add(QuotaMenu());
        menu.Items.Add(new ToolStripSeparator());
        var design=new ToolStripMenuItem("分级设计与署名");
        design.DropDownItems.Add("奶瓶数量：每瓶约 20% 剩余额度，向上取整").Enabled=false;
        design.DropDownItems.Add("额度只读本地日志；过重置时间等待新记录").Enabled=false;
        design.DropDownItems.Add("五档对应 1 / 1 / 2 / 2 / 3 行；从屁股向两侧展开").Enabled=false;
        design.DropDownItems.Add("五档边界：15 / 30 / 60 / 100 tok/s").Enabled=false;
        design.DropDownItems.Add("速度和密度连续平滑变化，不按行数表达总量").Enabled=false;
        design.DropDownItems.Add("速率与缓存来自最近完成响应，非瞬时值").Enabled=false;
        design.DropDownItems.Add("交互设计：Hziibepacient").Enabled=false;
        design.DropDownItems.Add("数据基础：drkang7 / Codex Token HUD · MIT").Enabled=false;
        menu.Items.Add(design);
        menu.Items.Add(ThreadMenu());
        var sizes=new ToolStripMenuItem("整体大小（也可滚轮调整）");
        foreach(double size in new[]{.7,1,1.3,1.6,2}){double chosen=size;sizes.DropDownItems.Add((size*100)+"%",null,(s,e)=>ResizeScene(chosen));}menu.Items.Add(sizes);
        var demos=new ToolStripMenuItem("试播动画");
        foreach(double t in new[]{4.0,3.0,2.0,1.2,.7}){double chosenSeconds=t;demos.DropDownItems.Add("第 "+(demos.DropDownItems.Count+1)+" 档 · "+t+" 秒穿过",null,(s,e)=>Demo(chosenSeconds));}menu.Items.Add(demos);
        var quotas=new ToolStripMenuItem("试播奶瓶额度");
        foreach(double q in new[]{100,75,50,20,0}){double selectedQuota=q;quotas.DropDownItems.Add(q+"%",null,(s,e)=>{Demo(2,selectedQuota);});}menu.Items.Add(quotas);
        menu.Items.Add("模拟结束 / 恢复真实数据",null,(s,e)=>{demoUntil=0;working=false;emitting=false;lastRead=DateTime.MinValue;Render();});
        var top=new ToolStripMenuItem("始终置顶");top.Checked=TopMost;top.Click+=(s,e)=>{TopMost=!TopMost;};menu.Items.Add(top);
        menu.Items.Add(visibleScene?"隐藏（托盘可恢复）":"显示",null,(s,e)=>{visibleScene=!visibleScene;if(visibleScene){Show();Render(true);}else Hide();});
        menu.Items.Add("退出",null,(s,e)=>BeginInvoke(new Action(Close)));return menu;
    }
    string PinnedThread(){
        try{var saved=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(runtime,"selection.json")));
            object id;return saved.TryGetValue("thread_id",out id)&&id!=null?id.ToString():"";
        }catch{return Value("pinned_thread_id");}
    }
    string FollowMode(){
        if(PinnedThread().Length>0)return "pinned";
        try{var saved=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(runtime,"selection.json")));
            object mode;return saved.TryGetValue("follow_mode",out mode)&&Convert.ToString(mode)=="current"?"current":"auto";
        }catch{return Value("follow_mode")=="current"?"current":"auto";}
    }
    ToolStripMenuItem ThreadMenu(){
        string pinned=PinnedThread(),mode=FollowMode();
        var menu=new ToolStripMenuItem("选择观察的对话");
        var automatic=new ToolStripMenuItem("自动跟随最新活动");automatic.Checked=mode=="auto";
        automatic.Click+=(s,e)=>Choose("");menu.DropDownItems.Add(automatic);
        var current=new ToolStripMenuItem("自动跟随当前对话");current.Checked=mode=="current";
        current.ToolTipText="跟随 Codex 窗口中正在查看的聊天；同名或无法识别时请固定选择";
        current.Click+=(s,e)=>Choose("","current");menu.DropDownItems.Add(current);
        menu.DropDownItems.Add(new ToolStripSeparator());
        object rows;if(state.TryGetValue("threads",out rows)&&rows is IEnumerable){foreach(var row in (IEnumerable)rows){
            var r=row as Dictionary<string,object>;if(r==null)continue;
            string id=r["id"].ToString(),title=r["title"].ToString();
            var item=new ToolStripMenuItem(title.Length>45?title.Substring(0,45)+"…":title);
            item.ToolTipText=id;item.Checked=mode=="pinned"&&id==pinned;item.Click+=(s,e)=>Choose(id);menu.DropDownItems.Add(item);
        }}
        return menu;
    }
    void Choose(string id,string mode="auto"){
        string path=Path.Combine(runtime,"selection.json"),temporary=path+".tmp";
        File.WriteAllText(temporary,json.Serialize(new{thread_id=id,follow_mode=id.Length>0?"pinned":mode}));
        if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        particles.Clear();lanes=0;demoUntil=0;lastRun="";
    }
    void Tick(){
        var now=DateTime.UtcNow;float dt=(float)Math.Min(.05,(now-previous).TotalSeconds);previous=now;
        if((now-lastRead).TotalSeconds>.09){lastRead=now;try{state=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(runtime,"scene.json")));}catch{}}
        double seconds=demoSeconds;
        if(demoUntil>Now()) {working=true;emitting=true;bottles=BottleCount(demoQuota);lanes=DemoLanes(demoSeconds);quotaPercent=(float)demoQuota;}
        else {
            bool fresh=Now()-Number("updated")<5;
            string run=Value("selected")+"/"+Value("run");if(lastRun!=run){particles.Clear();lanes=0;lastRun=run;}
            working=fresh&&Flag("working");emitting=fresh&&Flag("emitting");seconds=Number("seconds");
            bottles=QuotaKnown()?BottleCount(Number("quota_remaining")):0;
            quotaPercent=QuotaKnown()?(float)Number("quota_remaining"):0;
            int targetLanes=emitting?(int)Number("lanes"):0;
            if(targetLanes==0||lanes==0){lanes=targetLanes;pendingLanes=targetLanes;}
            else if(targetLanes!=lanes){if(pendingLanes!=targetLanes){pendingLanes=targetLanes;laneChangeAt=Now();}else if(Now()-laneChangeAt>.6)lanes=targetLanes;}
            else pendingLanes=lanes;
            status=fresh?Value("status"):"数据连接暂停 · 休息一下";
        }
        Animate(dt,seconds);
        if(visibleScene&&!dragging)Render();
    }
    static float Clamp01(float v){return v<0?0:v>1?1:v;}
    static float EaseOutBack(float t){t=Clamp01(t);float c=1.9f;return 1+(c+1)*(float)Math.Pow(t-1,3)+c*(float)Math.Pow(t-1,2);}
    bool Busy(){
        return emitting||particles.Count>0||drops.Count>0||puffs.Count>0||clock-poseSwitchAt<.6||Math.Abs(squashVel)>.02f||Math.Abs(squash)>.003f
            ||Math.Abs(recoilVel)>.2f||Math.Abs(tailVel)>.3f||Math.Abs(followVel)>.2f||(toolsBlend>.01f&&toolsBlend<.99f);
    }
    void Animate(float dt,double seconds){
        clock+=dt;
        float blend=1-(float)Math.Exp(-dt/.3);
        float target=emitting&&seconds>0?1/(float)seconds:0;
        motion+=(target-motion)*blend;
        float spacing=demoUntil>Now()?(float)Math.Max(25,Math.Min(70,demoSeconds*15+10)):(float)Number("spacing",70);
        smoothSpacing+=(spacing-smoothSpacing)*blend;
        // Pose change as a little jump: crouch, leap (swapping the drawing on the
        // way up), land with a squash and settle on the spring.
        if(working!=wasWorking){
            squashVel+=working?-2.4f:-1.8f;wasWorking=working;poseSwitchAt=clock;landed=false;
            // Back at rest, the finger goes straight back to the nose.
            if(!working){pickStart=clock+.6;pickLoops=1;nextPick=clock+5+random.NextDouble()*4;}
        }
        // A stream ending earns a sigh of relief; starting one is a small brace.
        if(wasEmitting&&!emitting)reliefUntil=clock+1.3;
        if(!wasEmitting&&emitting&&clock-poseSwitchAt>.4)squashVel-=1.2f;
        wasEmitting=emitting;
        // Milk gone from the bottles: the crocodile was drinking.
        string quotaWindow=Value("quota_window_minutes");
        if(lastQuotaWindow==quotaWindow&&lastQuota>=0&&bottles>0&&quotaPercent<lastQuota-.01f&&!emitting)drinkUntil=clock+1.7;
        lastQuotaWindow=quotaWindow;
        lastQuota=quotaPercent;
        double jump=clock-poseSwitchAt;
        hopY=jump>=.07&&jump<.31?-(working?8:6)*(float)Math.Sin(Math.PI*(jump-.07)/.24):0;
        if(!landed&&jump>=.31){landed=true;squashVel-=1.5f;}
        // Effort 0..1 across the speed tiers: about .3 at 40 tok/s, .6 at 80, 1 from 140.
        effort+=((emitting?Clamp01((motion-.2f)/.8f):0)-effort)*blend;
        lean+=((working?-1.2f-3.4f*effort:0)-lean)*blend;
        toolsBlend+=((working&&!emitting?1:0)-toolsBlend)*(1-(float)Math.Exp(-dt/.15));
        // Every poop is a shot: it leaves fast, and the body, tail and bottles take
        // the recoil. Single shots kick visibly; a steady stream becomes a held
        // forward push with a shudder, so kicks shrink as the shot rate rises.
        float speed=(EndX-ButtX)*motion;
        float shotRate=emitting?Math.Max(1,lanes)*speed/Math.Max(17,smoothSpacing):0;
        float kick=1/(1+.13f*shotRate);
        for(int lane=0;lane<lanes&&lane<3;lane++){
            if(!emitting||speed<=0)continue;
            laneEmission[lane]+=speed*dt/Math.Max(17,smoothSpacing);
            while(laneEmission[lane]>=1){
                laneEmission[lane]-=1;
                particles.Add(new Particle{X=ButtX+recoil,Y=ButtY,TargetY=LaneY[lane],Speed=speed*1.9f,Index=particles.Count+lane});
                recoilVel-=210*kick;tailVel+=220*kick;squashVel-=.9f*kick;
                if(kick>.55f)recoilPoseUntil=clock+.13;
                for(int i=0;i<5;i++)bottleTiltVel[i]+=((float)random.NextDouble()*2-1)*55*kick;
                if(clock-lastPuff>.05){lastPuff=clock;puffs.Add(new Puff{X=ButtX+4+recoil,Y=ButtY-2+(float)random.NextDouble()*4,Size=.8f+.4f*kick});}
            }
        }
        squashVel+=(-170*squash-13*squashVel)*dt;squash+=squashVel*dt;squash=Math.Max(-.12f,Math.Min(.12f,squash));
        recoilVel+=(-200*recoil-13*recoilVel)*dt;recoil=Math.Max(-14,Math.Min(5,recoil+recoilVel*dt));
        float tailGoal=working?2+6*effort:0;
        tailVel+=(110*(tailGoal-tail)-10*tailVel)*dt;tail=Math.Max(-8,Math.Min(20,tail+tailVel*dt));
        // The bottles drift after the head with a lag, so a lean or a kick reaches them late.
        float head=recoil+(float)Math.Sin((lean+recoil*.35f)*Math.PI/180)*(PivotY-80);
        followVel+=(55*(head*.9f-follow)-8*followVel)*dt;follow+=followVel*dt;
        for(int i=0;i<5;i++){bottleTiltVel[i]+=(-110*bottleTilt[i]-6*bottleTiltVel[i])*dt;bottleTilt[i]=Math.Max(-25,Math.Min(25,bottleTilt[i]+bottleTiltVel[i]*dt));}
        foreach(var f in puffs){f.Age+=dt;f.X+=26*dt;f.Y-=9*dt;}
        puffs.RemoveAll(f=>f.Age>.42f);
        // Nose picking now and then while resting.
        if(working)nextPick=Math.Max(nextPick,clock+2);
        else if(clock>=nextPick){pickStart=clock;pickLoops=random.NextDouble()<.4?2:1;nextPick=clock+4.5+random.NextDouble()*5;}
        foreach(var p in particles){
            p.Age+=dt;float desired=emitting?speed:p.Speed;p.Speed+=(desired-p.Speed)*blend;p.X+=p.Speed*dt;
            // Fan out from the butt into the lane over the first 46 px, then hop along it.
            float k=Clamp01((p.X-ButtX)/46f);p.Y=ButtY+(p.TargetY-ButtY)*k*k*(3-2*k);
            p.Hop+=p.Speed*dt/24f*(float)Math.PI;
        }
        particles.RemoveAll(p=>p.X>EndX+14);
        // Blinks every few seconds, sometimes twice.
        if(clock>=nextBlink){blinkStart=clock;doubleBlink=random.NextDouble()<.25;nextBlink=clock+2.2+random.NextDouble()*3.8;}
        // Sweat when running at the top tiers.
        if(effort>.62f&&clock>=nextDrop){nextDrop=clock+.55+random.NextDouble()*.5;drops.Add(new Drop{X=232+(float)random.NextDouble()*10,Y=75,VX=18+(float)random.NextDouble()*14,VY=-28});}
        foreach(var d in drops){d.Age+=dt;d.VY+=150*dt;d.X+=d.VX*dt;d.Y+=d.VY*dt;}
        drops.RemoveAll(d=>d.Age>.75f);
        // Bottles pop in and out on springs; the last bottle's milk level eases.
        for(int i=0;i<5;i++){float goal=i<bottles?1:0;bottleVel[i]+=(200*(goal-bottleScale[i])-14*bottleVel[i])*dt;bottleScale[i]=Math.Max(0,bottleScale[i]+bottleVel[i]*dt);}
        float fill=bottles>0?Math.Max(.1f,Math.Min(1,(quotaPercent-20*(bottles-1))/20f)):1;
        fillShown+=(fill-fillShown)*blend;
    }
    // Lids drop fast, hold, and lift a little slower.
    static float Lid(double t){return t<.05?(float)(t/.05):t<.08?1:t<.17?1-(float)((t-.08)/.09):0;}
    float Blink(){
        double t=clock-blinkStart;
        return Math.Max(Lid(t),doubleBlink&&t>=.22?Lid(t-.22):0);
    }
    Bitmap Pose(int index){return poseFrames.Length>index?poseFrames[index]:restFrames[0];}
    Bitmap CurrentFrame(){
        restShown=false;
        // Pose changes: crouch, then airborne, then the new pose.
        double jump=clock-poseSwitchAt;
        if(jump<.07)return Pose(Crouch);
        if(jump<.31)return Pose(Jump);
        if(working){
            if(emitting){
                // A full-speed stream holds the straining face; slower shots flash it.
                if(effort>.75f||clock<recoilPoseUntil)return Pose(Recoil);
                int best=0;for(int i=1;i<TailAngles.Length&&i<workFrames.Length;i++)if(Math.Abs(TailAngles[i]-tail)<Math.Abs(TailAngles[best]-tail))best=i;
                return workFrames[Math.Min(best,workFrames.Length-1)];
            }
            if(clock<reliefUntil)return Pose(Relief);
            if(clock<drinkUntil)return Pose(Drink);
            return Pose(Think);
        }
        if(clock<reliefUntil)return Pose(Relief);
        if(clock<drinkUntil)return Pose(Drink);
        restShown=true;
        int cycle=restFrames.Length-1,step=(int)((clock-pickStart)*15);
        return cycle>0&&step>=0&&step<cycle*pickLoops?restFrames[1+step%cycle]:restFrames[0];
    }
    static GraphicsPath RoundedRect(float x,float y,float w,float h,float radius){
        var path=new GraphicsPath();float d=radius*2;
        path.AddArc(x,y,d,d,180,90);path.AddArc(x+w-d,y,d,d,270,90);
        path.AddArc(x+w-d,y+h-d,d,d,0,90);path.AddArc(x,y+h-d,d,d,90,90);path.CloseFigure();return path;
    }
    string[] BarValues(){
        return new[]{QuotaKnown()?Number("quota_remaining").ToString("0.#")+"%":"待更新",
            ResetTime(false),
            Value("rate").Length>0?Number("rate").ToString("0.#")+" tok/s":"—",
            Value("cache_percent").Length>0?Number("cache_percent").ToString("0.#")+"%":"—"};
    }
    void DrawDataBar(Graphics g){
        const float x=60,y=202,w=300,h=30;
        using(var shadow=RoundedRect(x,y+1,w,h,5))
        using(var shadowBrush=new SolidBrush(Color.FromArgb(16,48,42,32)))g.FillPath(shadowBrush,shadow);
        using(var shape=RoundedRect(x,y,w,h,5))
        using(var fill=new SolidBrush(Color.FromArgb(245,250,248,242)))
        using(var border=new Pen(Color.FromArgb(180,193,187,177),.8f)){
            g.FillPath(fill,shape);g.DrawPath(border,shape);
        }
        float[] widths={55,65,115,65};
        using(var divider=new Pen(Color.FromArgb(85,193,187,177),.7f)){
            float separator=x;
            for(int i=0;i<widths.Length-1;i++){separator+=widths[i];g.DrawLine(divider,separator,y+8,separator,y+h-8);}
        }
        var values=BarValues();float left=x;
        using(var valueBrush=new SolidBrush(Color.FromArgb(58,59,56)))
        using(var format=new StringFormat(StringFormat.GenericTypographic){Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,FormatFlags=StringFormatFlags.NoWrap}){
            for(int i=0;i<values.Length;i++){
                if(barText[i]!=values[i]){
                    // Fit the entire value, including tok/s, once per new value; never truncate the unit.
                    float size=15;
                    while(size>9){using(var candidate=new Font("Microsoft YaHei UI",size,FontStyle.Bold,GraphicsUnit.Pixel)){
                        if(g.MeasureString(values[i],candidate,1000,format).Width<=widths[i]-12)break;
                    }size-=.5f;}
                    if(barFont[i]!=null)barFont[i].Dispose();
                    barFont[i]=new Font("Microsoft YaHei UI",size,FontStyle.Bold,GraphicsUnit.Pixel);barText[i]=values[i];
                }
                g.DrawString(values[i],barFont[i],valueBrush,new RectangleF(left+6,y,widths[i]-12,h),format);
                left+=widths[i];
            }
        }
    }
    void DrawBottles(Graphics g){
        // Five staggered slots in front of the snout; each floats on its own phase,
        // trails the head's lean and recoil, and wobbles when a shot lands.
        float[,] slots={{62,82,.28f},{80,110,-.12f},{52,136,.06f},{118,152,-.05f},{86,170,.14f}};
        int last=bottles-1;
        for(int i=0;i<5;i++){
            float s=bottleScale[i];if(s<.02f)continue;
            var saved=g.Save();
            g.TranslateTransform(slots[i,0]+follow*(.75f+.07f*i),slots[i,1]+(float)Math.Sin(clock*2.1+i*1.3)*1.4f-Math.Abs(bottleTilt[i])*.08f);
            g.RotateTransform(slots[i,2]*180/(float)Math.PI+(float)Math.Sin(clock*1.6+i*.9)*3+bottleTilt[i]);
            g.ScaleTransform(s,s);
            var box=new RectangleF(-10,-22,20,30);
            if(i==last&&fillShown<.995f){
                // Milk spans 52.5%..94% of the bottle drawing; above the level, show empty glass.
                g.DrawImage(bottleEmptyScaled,box);
                float top=box.Top+box.Height*.525f,bottom=box.Top+box.Height*.94f,level=top+(1-fillShown)*(bottom-top);
                g.SetClip(new RectangleF(box.Left-2,level,box.Width+4,box.Bottom-level+2),CombineMode.Intersect);
                g.DrawImage(bottleScaled,box);
                g.ResetClip();
                using(var surface=new Pen(Color.FromArgb(200,214,190,120),.9f))g.DrawLine(surface,-6.5f,level+.3f,6.5f,level+.3f);
            }
            else g.DrawImage(bottleScaled,box);
            g.Restore(saved);
        }
    }
    void DrawPoops(Graphics g,ImageAttributes fade,ColorMatrix alpha){
        float w=PoopW,h=PoopH;
        using(var shadow=new SolidBrush(Color.FromArgb(34,70,50,20)))
        foreach(var p in particles){
            float k=Clamp01((p.X-ButtX)/46f);if(k<.15f)continue;
            float hop=(float)Math.Abs(Math.Sin(p.Hop));
            float sw=w*(.8f-.25f*hop*k);g.FillEllipse(shadow,p.X-sw/2,p.Y-1.5f,sw,3.2f);
        }
        if(effort>.4f){
            using(var line=new Pen(Color.FromArgb((int)(40+70*effort),186,140,84),1.3f)){
                line.StartCap=line.EndCap=LineCap.Round;
                foreach(var p in particles){
                    if(p.X<ButtX+40)continue;
                    float len=6+10*effort,x=p.X-w/2-3;
                    g.DrawLine(line,x-len,p.Y-h*.55f,x,p.Y-h*.55f);g.DrawLine(line,x-len*.6f,p.Y-h*.25f,x-1,p.Y-h*.25f);
                }
            }
        }
        foreach(var p in particles){
            float k=Clamp01((p.X-ButtX)/46f);
            float hop=(float)Math.Abs(Math.Sin(p.Hop))*k;
            // Squash on landing, stretch in the air, and pop in with a slight overshoot.
            float contact=Clamp01(hop*3);float sy=.84f+.16f*contact,sx=1+(1-sy)*.9f;
            float grow=EaseOutBack(p.Age/.22f);
            float a=Clamp01((EndX+14-p.X)/26f);
            var saved=g.Save();
            g.TranslateTransform(p.X,p.Y-hop*3.2f+.5f);g.ScaleTransform(grow*sx,grow*sy);
            var box=new RectangleF(-w/2,-h,w,h);
            if(a<.99f){alpha.Matrix33=a;fade.SetColorMatrix(alpha);g.DrawImage(poopScaled,Rectangle.Round(box),0,0,poopScaled.Width,poopScaled.Height,GraphicsUnit.Pixel,fade);}
            else g.DrawImage(poopScaled,box);
            g.Restore(saved);
        }
        // A wavy whiff over every third poop.
        using(var whiff=new Pen(Color.FromArgb(110,140,166,86),1.2f)){
            whiff.StartCap=whiff.EndCap=LineCap.Round;
            foreach(var p in particles){
                if(p.Index%3!=0||p.Age<.4f||p.X>EndX-20)continue;
                float t=(float)((clock*1.4+p.Index*.37)%1),x=p.X-2,y=p.Y-h-3-t*7;
                int al=(int)(110*Math.Sin(t*Math.PI));whiff.Color=Color.FromArgb(al,140,166,86);
                g.DrawBezier(whiff,x,y+6,x-3,y+4,x+3,y+2,x,y);
                g.DrawBezier(whiff,x+5,y+7,x+2,y+5,x+8,y+3,x+5,y+1);
            }
        }
    }
    void DrawCroc(Graphics g){
        var saved=g.Save();
        float breath=(float)Math.Sin(clock*2*Math.PI/(working?1.3-.5*effort:3.4));
        float amp=working?.011f:.017f;
        float sy=1+squash+amp*breath,sx=1-squash*.6f-amp*.4f*breath;
        float bob=working?(float)Math.Sin(clock*(6+6*effort))*(.5f+.8f*effort):0;
        // A steady stream shudders the body slightly.
        float shudder=emitting?(float)Math.Sin(clock*47)*.5f*effort:0;
        g.TranslateTransform(PivotX+recoil+shudder,PivotY+bob+hopY);g.RotateTransform(lean+recoil*.35f);g.ScaleTransform(sx,sy);g.TranslateTransform(-PivotX,-PivotY);
        var box=new RectangleF(SpriteX,SpriteY,SpriteSize,SpriteSize);
        g.DrawImage(CurrentFrame(),box);
        // Only the resting face blinks; the other poses draw their own eyes.
        float closure=restShown?Blink():0;
        if(closure>.02f){
            var eyes=RestEyes;
            using(var lid=new Pen(Outline,2.6f)){
                lid.StartCap=lid.EndCap=LineCap.Round;
                for(int i=0;i<2;i++){
                    float x=box.X+eyes[i,0]*SpriteSize,y=box.Y+eyes[i,1]*SpriteSize,ew=(eyes[i,2]-eyes[i,0])*SpriteSize,eh=(eyes[i,3]-eyes[i,1])*SpriteSize;
                    // The lid is lit like the head: light on top, shade below.
                    using(var skin=new LinearGradientBrush(new RectangleF(x,y-4,ew,eh+8),LidTop,LidBottom,90f))
                    using(var eye=new GraphicsPath()){
                        // Slightly larger than the white, still inside the thick eye ring.
                        eye.AddEllipse(x-2.2f,y-2.2f,ew+4.4f,eh+4.4f);
                        var inner=g.Save();g.SetClip(eye,CombineMode.Intersect);
                        g.FillRectangle(skin,x-4,y-4,ew+8,(eh+8)*Math.Min(1,closure*1.15f));
                        g.Restore(inner);
                        // Closed lids read as a soft downward curve.
                        if(closure>.55f)g.DrawArc(lid,x+ew*.18f,y+eh*.2f,ew*.64f,eh*.45f,25,130);
                    }
                }
            }
        }
        g.Restore(saved);
    }
    void DrawShadow(Graphics g){
        // Soft contact shadow; it shrinks and fades while the crocodile is in the air.
        float lift=Clamp01(-hopY/8),w=104*(1-.25f*lift),h=13*(1-.25f*lift),cx=PivotX+recoil*.6f,cy=PivotY+1.5f;
        using(var path=new GraphicsPath()){
            path.AddEllipse(cx-w/2,cy-h/2,w,h);
            using(var brush=new PathGradientBrush(path)){
                brush.CenterColor=Color.FromArgb((int)(70*(1-.5f*lift)),58,70,40);brush.SurroundColors=new[]{Color.FromArgb(0,58,70,40)};
                g.FillPath(brush,path);
            }
        }
    }
    void DrawEffects(Graphics g){
        // Puffs at the butt for each shot.
        using(var puff=new SolidBrush(Color.Transparent))
        foreach(var f in puffs){
            float t=f.Age/.42f,r=(2.5f+9*t)*f.Size;
            puff.Color=Color.FromArgb((int)(170*(1-t)),204,188,156);
            g.FillEllipse(puff,f.X-r,f.Y-r,r*2,r*2);
            g.FillEllipse(puff,f.X+r*.5f,f.Y-r*1.2f,r*1.2f,r*1.2f);
        }
        // Sweat drops at the top tiers.
        using(var fill=new SolidBrush(Color.FromArgb(200,160,214,240)))
        using(var edge=new Pen(Color.FromArgb(200,76,136,176),1.1f))
        foreach(var d in drops){
            int a=(int)(220*Clamp01(1-d.Age/.75f));fill.Color=Color.FromArgb(a,160,214,240);edge.Color=Color.FromArgb(a,76,136,176);
            using(var path=new GraphicsPath()){
                path.AddBezier(d.X,d.Y-5,d.X+1.2f,d.Y-2.5f,d.X+3,d.Y-.5f,d.X+3,d.Y+1);
                path.AddArc(d.X-3,d.Y-2,6,6,0,180);
                path.AddBezier(d.X-3,d.Y+1,d.X-3,d.Y-.5f,d.X-1.2f,d.Y-2.5f,d.X,d.Y-5);
                g.FillPath(fill,path);g.DrawPath(edge,path);
            }
        }
        // Thinking dots while tools run (working, no new tokens).
        if(toolsBlend>.02f){
            using(var dot=new SolidBrush(Color.FromArgb((int)(210*toolsBlend),Outline)))
            for(int i=0;i<3;i++){
                float lift=(float)Math.Max(0,Math.Sin(clock*5.5-i*.9))*3;
                float x=266+i*9,y=50-i*5-lift,r=2.4f+i*.4f;
                g.FillEllipse(dot,x-r,y-r,r*2,r*2);
            }
        }
    }
    void Render(bool force=false){
        if(!loaded||Width<1||Height<1)return;
        // Resting breath and blinks need ~15 fps; motion gets every ~30 fps tick.
        if(!force&&!Busy()&&clock-lastRender<.066)return;
        lastRender=clock;
        EnsureSprites();EnsureSurface();
        using(var g=Graphics.FromImage(frame))
        using(var fade=new ImageAttributes()){
            g.Clear(Color.Transparent);g.ScaleTransform((float)scale,(float)scale);
            g.InterpolationMode=InterpolationMode.Bilinear;g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.Half;
            DrawShadow(g);
            DrawBottles(g);
            DrawPoops(g,fade,new ColorMatrix());
            // Paint the character after the poops so they emerge from behind it.
            DrawCroc(g);
            DrawEffects(g);
            DrawDataBar(g);
        }
        IntPtr screen=GetDC(IntPtr.Zero);
        try{var pos=new PointNative(Left,Top);var size=new SizeNative(Width,Height);var source=new PointNative(0,0);var blend=new Blend{Op=0,Flags=0,Alpha=255,Format=1};if(!UpdateLayeredWindow(Handle,screen,ref pos,ref size,surfaceDc,ref source,0,ref blend,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
        finally{ReleaseDC(IntPtr.Zero,screen);}
    }
    protected override void WndProc(ref Message m){
        if(m.Msg==0x84&&frame!=null){long value=m.LParam.ToInt64();var p=PointToClient(new Point((short)(value&65535),(short)((value>>16)&65535)));if(p.X>=0&&p.Y>=0&&p.X<frame.Width&&p.Y<frame.Height&&frame.GetPixel(p.X,p.Y).A<15){m.Result=new IntPtr(-1);return;}}
        base.WndProc(ref m);
    }
    [STAThread] static void Main(string[] args){
        bool created;using(var mutex=new System.Threading.Mutex(true,"Local\\CrocTokenDesktop-"+args[0].GetHashCode(),out created)){
            if(!created)return;SetProcessDPIAware();Application.EnableVisualStyles();
            try{Application.Run(new CrocDesktop(args[0],args[1],Array.IndexOf(args,"--demo")>=0,Array.IndexOf(args,"--smoke")>=0));}
            catch(Exception e){File.WriteAllText(Path.Combine(args[1],"desktop-error.txt"),e.ToString());MessageBox.Show(e.Message,"桌面鳄鱼启动失败");}
        }
    }
}
