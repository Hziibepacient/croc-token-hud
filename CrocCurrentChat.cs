using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CodexTokenHud {
    // Separate worker: a slow UI Automation provider must not freeze animation or collection.
    internal static class CrocCurrentChat {
        [STAThread] static void Main(string[] args) {
            string runtime=args[0];bool once=args.Length>1&&args[1]=="--once";
            int parent=once?0:int.Parse(args[1]);ViewSnapshot previous=null;
            while(true){
                if(!once){
                    try{using(var owner=Process.GetProcessById(parent)){if(owner.HasExited)return;}}catch{return;}
                    if(Json.Text(Json.Read(Path.Combine(runtime,"selection.json")),"follow_mode")!="current")return;
                }
                string title="";
                try{
                    var data=Json.Read(Path.Combine(runtime,"scene.json"));
                    var titles=new HashSet<string>(Json.List(data,"threads").Select(row=>Json.Text(row,"title")));
                    previous=Inspector.Find(previous,titles);
                    if(previous!=null&&previous.Primary)title=previous.Title??"";
                }catch{previous=null;}
                Json.Write(Path.Combine(runtime,"current-chat.json"),new Dictionary<string,object>{
                    {"title",title},{"updated_at",(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds}});
                if(once)return;
                Thread.Sleep(700);
            }
        }
    }
}
