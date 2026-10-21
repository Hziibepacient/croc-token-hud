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
    readonly string root, runtime;
    readonly JavaScriptSerializer json=new JavaScriptSerializer();
    readonly Timer timer=new Timer();
    readonly ContextMenuStrip contextMenu=new ContextMenuStrip();
    Image rest,work,bottle,poop;
    Bitmap frame;
    NotifyIcon tray;
    Dictionary<string,object> state=new Dictionary<string,object>();
    List<Particle> particles=new List<Particle>();
    string lastRun="", status="正在连接本地日志";
    double scale=1, demoUntil=0, demoSeconds=2, demoQuota=100, laneChangeAt=0;
    readonly double[] laneEmission={0,.33,.66};
    float motion=0, smoothSpacing=50;
    readonly float[] rowStarts=new float[5];
    int bottles=0, lanes=0;
    int pendingLanes=0;
    bool working=false,emitting=false,dragging=false,loaded=false,visibleScene=true;
    DateTime previous=DateTime.UtcNow, lastRead=DateTime.MinValue;
    class Particle { public float X,Y,Speed,Age,Phase; public int Row; }
    float RowY(int row){return 80+row*22;}
    float FrontEdge(float y){
        var bitmap=(Bitmap)work;
        int py=Math.Max(0,Math.Min(bitmap.Height-1,(int)((y-35)*bitmap.Height/164)));
        for(int x=0;x<bitmap.Width;x++)if(bitmap.GetPixel(x,py).A>128)return 125+x*164f/bitmap.Width;
        return 155;
    }
    static readonly int[] LaneOrder={3,2,4,1,0}; // Butt first, then spread above/below.
    float RowStart(int row){
        if(rowStarts[row]>0)return rowStarts[row];
        // Follow the actual back/tail contour, instead of a fixed empty column.
        var bitmap=(Bitmap)work;
        int py=Math.Max(0,Math.Min(bitmap.Height-1,(int)((RowY(row)-35)*bitmap.Height/164)));
        for(int x=bitmap.Width-1;x>=0;x--)if(bitmap.GetPixel(x,py).A>128){rowStarts[row]=125+x*164f/bitmap.Width-4;return rowStarts[row];}
        return 250;
    }
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
        rest=Image.FromFile(Path.Combine(root,"web/assets/croc-rest.png"));work=Image.FromFile(Path.Combine(root,"web/assets/croc-work.png"));
        bottle=Image.FromFile(Path.Combine(root,"web/assets/bottle.png"));poop=Image.FromFile(Path.Combine(root,"web/assets/poop.png"));
        var area=Screen.PrimaryScreen.WorkingArea;Location=new Point(area.Right-450,area.Bottom-230);
        try{var saved=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(runtime,"layout.json")));scale=Convert.ToDouble(saved["scale"]);Location=new Point(Convert.ToInt32(saved["x"]),Convert.ToInt32(saved["y"]));}catch{}
        scale=Math.Max(.7,Math.Min(2,scale));ClientSize=new Size((int)(420*scale),(int)(244*scale));
        bool onScreen=false;foreach(var screen in Screen.AllScreens)if(screen.WorkingArea.IntersectsWith(Bounds))onScreen=true;
        if(!onScreen)Location=new Point(area.Right-Width-20,area.Bottom-Height-20);
        tray=new NotifyIcon();tray.Icon=SystemIcons.Application;tray.Text="桌面鳄鱼 · 右键设置";tray.Visible=true;
        tray.MouseClick+=(s,e)=>{if(e.Button==MouseButtons.Right)Popup().Show(Cursor.Position);else if(e.Button==MouseButtons.Left){visibleScene=true;Show();Render();}};
        MouseDown+=(s,e)=>{if(e.Button==MouseButtons.Left){dragging=true;ReleaseCapture();SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);dragging=false;Save();}else if(e.Button==MouseButtons.Right)Popup().Show(Cursor.Position);};
        MouseWheel+=(s,e)=>ResizeScene(scale+(e.Delta>0?.1:-.1));
        timer.Interval=25;timer.Tick+=(s,e)=>Tick();
        Shown+=(s,e)=>{loaded=true;if(demo)Demo(smoke?.7:2,smoke?(double?)100:null);timer.Start();Render();};
        if(smoke){var check=new Timer();check.Interval=1000;check.Tick+=(s,e)=>{
            check.Stop();timer.Stop();
            try{
                if(!IsWindowVisible(Handle)||frame==null||!working||bottles!=5||lanes!=3)throw new Exception("Working window state invalid");
                if(frame.GetPixel(0,0).A!=0)throw new Exception("Background is not transparent");
                if(frame.GetPixel((int)(90*scale),(int)(216*scale)).A<200)throw new Exception("Bottom data bar missing");
                if(BottleCount(0)!=0||BottleCount(20)!=1||BottleCount(74)!=4||BottleCount(100)!=5)throw new Exception("Quota bottle mapping invalid");
                state["quota_status"]="recorded";state["quota_remaining"]=54;state["quota_resets_at"]=Now()+3600;state["rate"]=42;
                var bar=BarValues();if(bar[0]!="54%"||bar[2]!="42 tok/s")throw new Exception("Data bar values invalid");
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
                File.WriteAllText(Path.Combine(runtime,"smoke-result.json"),json.Serialize(new{visible=true,transparent=true,working=true,restored=true,menuCycles=8,modelAndCache=true,fiveSpeedTiers=true,bottleCountPreserved=true,bottomDataBar=true,connectingWakes=true,width=Width,height=Height}));
            }catch(Exception error){File.WriteAllText(Path.Combine(runtime,"smoke-result.json"),json.Serialize(new{error=error.ToString()}));}
            Close();check.Dispose();};Shown+=(s,e)=>check.Start();}
        FormClosed+=(s,e)=>{timer.Stop();Save();tray.Dispose();contextMenu.Dispose();rest.Dispose();work.Dispose();bottle.Dispose();poop.Dispose();if(frame!=null)frame.Dispose();};
    }
    void Save(){try{File.WriteAllText(Path.Combine(runtime,"layout.json"),json.Serialize(new{ x=Left,y=Top,scale=scale}));}catch{}}
    void ResizeScene(double value){scale=Math.Max(.7,Math.Min(2,value));ClientSize=new Size((int)(420*scale),(int)(244*scale));Save();Render();}
    static int BottleCount(double percent){return (int)Math.Ceiling(Math.Max(0,Math.Min(100,percent))/20);}
    static int DemoLanes(double seconds){return seconds>=3?1:seconds>=1.2?2:3;}
    void Demo(double seconds,double? quota=null){particles.Clear();demoSeconds=seconds;demoQuota=quota??(demoUntil>Now()?demoQuota:bottles*20);demoUntil=Now()+12;working=true;emitting=true;bottles=BottleCount(demoQuota);lanes=DemoLanes(seconds);motion=0;status="模拟演示 · 12 秒后恢复真实数据";}
    string Clock(double seconds){try{return new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime().ToString("HH:mm");}catch{return "未知";}}
    bool QuotaKnown(){return Value("quota_status")=="recorded"&&Number("quota_resets_at")>Now();}
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
        menu.Items.Add("5 小时剩余："+(QuotaKnown()?Number("quota_remaining").ToString("0.#")+"%":"未知 / 等待更新")).Enabled=false;
        menu.Items.Add("额度重置："+(Number("quota_resets_at")>0?Clock(Number("quota_resets_at")):"未知")+"（本机时间）").Enabled=false;
        menu.Items.Add("本地额度记录："+(Number("quota_recorded_at")>0?Clock(Number("quota_recorded_at")/1000):"暂无")).Enabled=false;
        menu.Items.Add("输出吞吐："+(Value("rate").Length>0?Number("rate").ToString("0.#")+" tok/s（最近响应平均）":"等待计数")).Enabled=false;
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
        var threads=new ToolStripMenuItem("选择观察的对话");
        threads.DropDownItems.Add("自动跟随最新活动",null,(s,e)=>Choose(""));
        object rows;if(state.TryGetValue("threads",out rows)){foreach(var row in (IEnumerable)rows){var r=row as Dictionary<string,object>;if(r==null)continue;string id=r["id"].ToString(),title=r["title"].ToString();var item=new ToolStripMenuItem(title.Length>45?title.Substring(0,45)+"…":title);item.ToolTipText=id;item.Checked=id==Value("selected");item.Click+=(s,e)=>Choose(id);threads.DropDownItems.Add(item);}}
        menu.Items.Add(threads);
        var sizes=new ToolStripMenuItem("整体大小（也可滚轮调整）");
        foreach(double size in new[]{.7,1,1.3,1.6,2}){double chosen=size;sizes.DropDownItems.Add((size*100)+"%",null,(s,e)=>ResizeScene(chosen));}menu.Items.Add(sizes);
        var demos=new ToolStripMenuItem("试播动画");
        foreach(double t in new[]{4.0,3.0,2.0,1.2,.7}){double chosenSeconds=t;demos.DropDownItems.Add("第 "+(demos.DropDownItems.Count+1)+" 档 · "+t+" 秒穿过",null,(s,e)=>Demo(chosenSeconds));}menu.Items.Add(demos);
        var quotas=new ToolStripMenuItem("试播奶瓶额度");
        foreach(double q in new[]{100,75,50,20,0}){double selectedQuota=q;quotas.DropDownItems.Add(q+"%",null,(s,e)=>{Demo(2,selectedQuota);});}menu.Items.Add(quotas);
        menu.Items.Add("模拟结束 / 恢复真实数据",null,(s,e)=>{demoUntil=0;working=false;emitting=false;lastRead=DateTime.MinValue;Render();});
        var top=new ToolStripMenuItem("始终置顶");top.Checked=TopMost;top.Click+=(s,e)=>{TopMost=!TopMost;};menu.Items.Add(top);
        menu.Items.Add(visibleScene?"隐藏（托盘可恢复）":"显示",null,(s,e)=>{visibleScene=!visibleScene;if(visibleScene)Show();else Hide();});
        menu.Items.Add("退出",null,(s,e)=>BeginInvoke(new Action(Close)));return menu;
    }
    void Choose(string id){File.WriteAllText(Path.Combine(runtime,"selection.json"),json.Serialize(new{thread_id=id}));particles.Clear();bottles=0;lanes=0;demoUntil=0;lastRun="";}
    void Tick(){
        var now=DateTime.UtcNow;float dt=(float)Math.Min(.05,(now-previous).TotalSeconds);previous=now;
        if((now-lastRead).TotalSeconds>.09){lastRead=now;try{state=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(runtime,"scene.json")));}catch{}}
        double seconds=demoSeconds;
        if(demoUntil>Now()) {working=true;emitting=true;bottles=BottleCount(demoQuota);lanes=DemoLanes(demoSeconds);}
        else {
            bool fresh=Now()-Number("updated")<5;
            string run=Value("selected")+"/"+Value("run");if(lastRun!=run){particles.Clear();lanes=0;lastRun=run;}
            working=fresh&&Flag("working");emitting=fresh&&Flag("emitting");seconds=Number("seconds");
            bottles=QuotaKnown()?BottleCount(Number("quota_remaining")):0;
            int targetLanes=emitting?(int)Number("lanes"):0;
            if(targetLanes==0||lanes==0){lanes=targetLanes;pendingLanes=targetLanes;}
            else if(targetLanes!=lanes){if(pendingLanes!=targetLanes){pendingLanes=targetLanes;laneChangeAt=Now();}else if(Now()-laneChangeAt>.6)lanes=targetLanes;}
            else pendingLanes=lanes;
            status=fresh?Value("status"):"数据连接暂停 · 休息一下";
        }
        float end=408,blend=1-(float)Math.Exp(-dt/.3);
        float target=emitting&&seconds>0?1/(float)seconds:0;
        motion+=(target-motion)*blend;
        float spacing=demoUntil>Now()?(float)Math.Max(25,Math.Min(70,demoSeconds*15+10)):(float)Number("spacing",70);
        smoothSpacing+=(spacing-smoothSpacing)*blend;
        for(int lane=0;lane<lanes;lane++){
            int row=LaneOrder[lane];float start=RowStart(row),speed=(end-start)*motion;
            if(emitting&&speed>0){laneEmission[lane]+=speed*dt/Math.Max(17,smoothSpacing);while(laneEmission[lane]>=1){laneEmission[lane]-=1;particles.Add(new Particle{X=start,Y=RowY(row),Speed=speed,Row=row,Phase=lane*1.7f});}}
        }
        foreach(var p in particles){p.Age+=dt;float desired=emitting?(end-RowStart(p.Row))*motion:p.Speed;p.Speed+=(desired-p.Speed)*blend;p.X+=p.Speed*dt;p.Y=RowY(p.Row)+(float)Math.Sin(p.Age*5+p.Phase)*.7f;}
        particles.RemoveAll(p=>p.X>end+12);
        if(visibleScene&&!dragging)Render();
    }
    static GraphicsPath RoundedRect(float x,float y,float w,float h,float radius){
        var path=new GraphicsPath();float d=radius*2;
        path.AddArc(x,y,d,d,180,90);path.AddArc(x+w-d,y,d,d,270,90);
        path.AddArc(x+w-d,y+h-d,d,d,0,90);path.AddArc(x,y+h-d,d,d,90,90);path.CloseFigure();return path;
    }
    string[] BarValues(){
        return new[]{QuotaKnown()?Number("quota_remaining").ToString("0.#")+"%":"待更新",
            Number("quota_resets_at")>0?Clock(Number("quota_resets_at")):"—",
            Value("rate").Length>0?Number("rate").ToString("0.#")+" tok/s":"—"};
    }
    void DrawDataBar(Graphics g){
        const float x=80,y=202,w=260,h=30;
        using(var shadow=RoundedRect(x,y+1,w,h,5))
        using(var shadowBrush=new SolidBrush(Color.FromArgb(16,48,42,32)))g.FillPath(shadowBrush,shadow);
        using(var shape=RoundedRect(x,y,w,h,5))
        using(var fill=new SolidBrush(Color.FromArgb(245,250,248,242)))
        using(var border=new Pen(Color.FromArgb(180,193,187,177),.8f)){
            g.FillPath(fill,shape);g.DrawPath(border,shape);
        }
        float[] widths={60,70,130};
        using(var divider=new Pen(Color.FromArgb(85,193,187,177),.7f)){
            g.DrawLine(divider,x+60,y+8,x+60,y+h-8);
            g.DrawLine(divider,x+130,y+8,x+130,y+h-8);
        }
        var values=BarValues();float left=x;
        using(var valueBrush=new SolidBrush(Color.FromArgb(58,59,56)))
        using(var format=new StringFormat(StringFormat.GenericTypographic){Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center,FormatFlags=StringFormatFlags.NoWrap}){
            for(int i=0;i<3;i++){
                float size=15;
                // Fit the entire value, including tok/s, before drawing; never truncate the unit.
                while(size>9){using(var candidate=new Font("Microsoft YaHei UI",size,FontStyle.Bold,GraphicsUnit.Pixel)){
                    if(g.MeasureString(values[i],candidate,1000,format).Width<=widths[i]-12)break;
                }size-=.5f;}
                using(var font=new Font("Microsoft YaHei UI",size,FontStyle.Bold,GraphicsUnit.Pixel))
                    g.DrawString(values[i],font,valueBrush,new RectangleF(left+6,y,widths[i]-12,h),format);
                left+=widths[i];
            }
        }
    }
    void Render(){
        if(!loaded||Width<1||Height<1)return;
        var image=new Bitmap(Width,Height,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(image)){
            g.Clear(Color.Transparent);g.ScaleTransform((float)scale,(float)scale);g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.SmoothingMode=SmoothingMode.AntiAlias;
            Image sprite=working?work:rest;
            float bob=working?(float)Math.Sin(Now()*5)*1.1f:0;
            // Five stable staggered slots, all inside the crocodile's head/foot height.
            float[,] slots={{125,84,.28f},{132,114,-.12f},{110,136,.06f},{149,150,-.05f},{118,174,.14f}};
            for(int i=0;i<Math.Min(5,bottles);i++){
                var saved=g.Save();g.TranslateTransform(slots[i,0],slots[i,1]);g.RotateTransform(slots[i,2]*180/(float)Math.PI);
                g.DrawImage(bottle,new RectangleF(-10,-22,20,30),new RectangleF(274,111,706,1038),GraphicsUnit.Pixel);g.Restore(saved);
            }
            // Soft connecting wakes and travelling dashes keep sparse particles
            // readable as a flow without covering the original cartoon sprite.
            for(int row=0;row<5;row++){
                var lane=particles.FindAll(p=>p.Row==row);
                if(lane.Count==0)continue;
                lane.Sort((a,b)=>a.X.CompareTo(b.X));
                float strength=Math.Min(1,lane[0].Speed/200f);
                using(var wake=new Pen(Color.FromArgb((int)(30+strength*35),176,130,62),2f)){
                    wake.StartCap=LineCap.Round;wake.EndCap=LineCap.Round;
                    for(int i=1;i<lane.Count;i++){
                        float from=lane[i-1].X+7,to=lane[i].X-8;
                        if(to>from&&to-from<80)g.DrawLine(wake,from,lane[i].Y,to,lane[i].Y);
                    }
                }
                foreach(var p in lane){
                    float length=9+strength*15;
                    using(var streak=new Pen(Color.FromArgb((int)(75+strength*65),174,119,47),1.6f)){
                        streak.StartCap=LineCap.Round;streak.EndCap=LineCap.Round;
                        g.DrawLine(streak,p.X-10-length,p.Y-3,p.X-11,p.Y-3);
                        g.DrawLine(streak,p.X-9-length*.65f,p.Y+3,p.X-12,p.Y+3);
                    }
                }
                // These highlights move along the connecting wake, rather than
                // blinking in place; they remain behind the character below.
                using(var glint=new Pen(Color.FromArgb(125,221,178,92),2f)){
                    glint.StartCap=LineCap.Round;glint.EndCap=LineCap.Round;
                    float origin=RowStart(row),limit=lane[lane.Count-1].X-8;
                    float phase=(float)((Now()*lane[0].Speed)%35);
                    for(float x=origin-35+phase;x<limit;x+=35){
                        float a=Math.Max(origin,x),b=Math.Min(limit,x+7);
                        if(b>a)g.DrawLine(glint,a,RowY(row),b,RowY(row));
                    }
                }
            }
            foreach(var p in particles){
                float fade=Math.Max(0,Math.Min(1,(420-p.X)/20));
                using(var attr=new ImageAttributes()){
                    var alpha=new ColorMatrix();alpha.Matrix33=fade;attr.SetColorMatrix(alpha);
                    g.DrawImage(poop,new Rectangle((int)p.X-10,(int)p.Y-6,20,12),337,365,770,478,GraphicsUnit.Pixel,attr);
                }
            }
            // Paint the character last so new particles emerge from behind it.
            g.DrawImage(sprite,new RectangleF(125,35+bob,164,164));
            DrawDataBar(g);

        }
        IntPtr screen=GetDC(IntPtr.Zero),dc=CreateCompatibleDC(screen),bitmap=image.GetHbitmap(Color.FromArgb(0)),old=SelectObject(dc,bitmap);
        try{var pos=new PointNative(Left,Top);var size=new SizeNative(Width,Height);var source=new PointNative(0,0);var blend=new Blend{Op=0,Flags=0,Alpha=255,Format=1};if(!UpdateLayeredWindow(Handle,screen,ref pos,ref size,dc,ref source,0,ref blend,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
        finally{SelectObject(dc,old);DeleteObject(bitmap);DeleteDC(dc);ReleaseDC(IntPtr.Zero,screen);}
        if(frame!=null)frame.Dispose();frame=image;
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
