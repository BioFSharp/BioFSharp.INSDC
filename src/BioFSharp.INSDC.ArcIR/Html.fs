namespace Arc.Build

open System.IO
open System.Text

open Arc.Build.GraphText

/// Serializes an [ArcIR] property graph to a single self-contained interactive HTML page: an embedded
/// force-directed SVG graph (no external scripts, CDN, or network) where nodes are colored by `Kind`,
/// edges are labeled by predicate, and clicking a node opens a side panel listing its full properties
/// and rendered annotations. The graph is embedded as a JSON literal; all rendering JS/CSS is inline, so
/// the file works offline. Relations pointing at ids absent from `Objects` become `Missing` placeholder
/// nodes. Pure — no dependency beyond the BCL.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Html =

    /// JSON string literal, safe to embed inside an HTML `<script>` block ('<' is escaped so a value can
    /// never break out of the tag or inject markup).
    let private jsonString (s: string) =
        let sb = StringBuilder()
        sb.Append '"' |> ignore

        for c in s do
            (match c with
             | '"' -> sb.Append "\\\""
             | '\\' -> sb.Append "\\\\"
             | '\n' -> sb.Append "\\n"
             | '\r' -> sb.Append "\\r"
             | '\t' -> sb.Append "\\t"
             | '<' -> sb.Append "\\u003c"
             | c when c < ' ' -> sb.Append(sprintf "\\u%04x" (int c))
             | c -> sb.Append c)
            |> ignore

        sb.Append '"' |> ignore
        sb.ToString()

    let private nonEmpty pairs =
        pairs |> Seq.filter (fun (_, v) -> v <> "")

    let private pairArray pairs =
        pairs
        |> nonEmpty
        |> Seq.map (fun (k, v) -> sprintf "[%s,%s]" (jsonString k) (jsonString v))
        |> String.concat ","
        |> sprintf "[%s]"

    let private nodeJson id label kind dtypes props anns =
        sprintf
            "{\"id\":%s,\"label\":%s,\"kind\":%s,\"dtypes\":%s,\"props\":%s,\"annotations\":%s}"
            (jsonString id)
            (jsonString label)
            (jsonString kind)
            (jsonString dtypes)
            (pairArray props)
            (pairArray anns)

    /// The graph as a JS/JSON object literal: `{ nodes: [...], edges: [...] }`.
    let private dataJson (ir: ArcIR) =
        let objectNode (o: ArcObject) =
            let dtypes = o.DTypes |> Seq.map (fun i -> localName i.Value) |> Seq.sort |> String.concat " "
            let props = o.Properties |> Seq.map (fun kv -> localName kv.Key.Value, renderValue kv.Value)
            let anns = o.Annotations |> Seq.map (fun a -> annotationName a, renderAnnotationValue a.Value)
            nodeJson o.Id.Value (nodeLabel o) (kindName o.Kind) dtypes props anns

        let missing =
            ir.Relations
            |> Seq.collect (fun r -> [ r.Subject; r.Object ])
            |> Seq.distinct
            |> Seq.filter (fun id -> not (ir.Objects.ContainsKey id))
            |> Seq.sort
            |> Seq.map (fun id -> nodeJson id.Value id.Value "Missing" "" Seq.empty Seq.empty)

        let nodes = Seq.append (ir.Objects.Values |> Seq.map objectNode) missing |> String.concat ","

        let edges =
            ir.Relations
            |> Seq.map (fun r ->
                sprintf
                    "{\"source\":%s,\"target\":%s,\"predicate\":%s}"
                    (jsonString r.Subject.Value)
                    (jsonString r.Object.Value)
                    (jsonString (localName r.Predicate.Value)))
            |> String.concat ","

        sprintf "{\"nodes\":[%s],\"edges\":[%s]}" nodes edges

    let private template =
        """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ArcIR graph</title>
<style>
*{box-sizing:border-box}
html,body{margin:0;height:100%;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif}
#app{display:flex;height:100vh}
#main{flex:1;position:relative;min-width:0}
#graph{width:100%;height:100%;background:#fafafa;cursor:grab;display:block}
#graph:active{cursor:grabbing}
.edge{stroke:#d0d0d0;stroke-width:1}
.elabel{fill:#a0a0a0;font-size:9px;pointer-events:none}
.nlabel{fill:#333;font-size:11px;pointer-events:none}
.node{cursor:pointer}
.node circle{stroke:#fff;stroke-width:1.5}
.node.sel circle{stroke:#222;stroke-width:2.5}
#panel{width:340px;border-left:1px solid #e3e3e3;overflow:auto;padding:16px;background:#fff}
#panel h2{font-size:15px;margin:0 0 8px;word-break:break-all}
#panel h3{font-size:11px;text-transform:uppercase;letter-spacing:.05em;color:#888;margin:16px 0 6px}
.badge{display:inline-block;color:#fff;font-size:11px;padding:2px 9px;border-radius:10px;margin-bottom:10px}
table{width:100%;border-collapse:collapse;font-size:12px}
td{padding:3px 6px;border-bottom:1px solid #f0f0f0;vertical-align:top;word-break:break-word}
td.k{color:#666;white-space:nowrap;width:38%}
#bar{position:absolute;top:10px;left:10px;font-size:12px;color:#555;background:rgba(255,255,255,.88);padding:6px 10px;border-radius:6px;box-shadow:0 1px 3px rgba(0,0,0,.12)}
#bar button{font-size:11px;margin-left:8px;cursor:pointer}
.hint{color:#aaa}
#legend{position:absolute;bottom:10px;left:10px;background:rgba(255,255,255,.92);padding:8px 10px;border-radius:6px;font-size:11px;box-shadow:0 1px 3px rgba(0,0,0,.12)}
#legend div{display:flex;align-items:center;margin:2px 0}
#legend span{width:11px;height:11px;border-radius:50%;display:inline-block;margin-right:6px}
@media (prefers-color-scheme:dark){
 html,body{background:#1e1e1e;color:#ddd}
 #graph{background:#1e1e1e}
 .edge{stroke:#3a3a3a}.elabel{fill:#777}.nlabel{fill:#ccc}
 #panel{background:#252525;border-color:#333}
 #panel h2{color:#eee}
 td{border-color:#333}td.k{color:#999}
 #bar,#legend{background:rgba(42,42,42,.92);color:#ccc}
}
</style>
</head>
<body>
<div id="app">
 <div id="main">
  <svg id="graph"><g id="viewport"></g></svg>
  <div id="bar"><span id="counts"></span><button id="reset">Reset view</button><span class="hint"> &middot; scroll = zoom, drag background = pan, drag node = move, click node = details</span></div>
  <div id="legend"></div>
 </div>
 <aside id="panel"><h2>ArcIR graph</h2><p class="hint">Click a node to see its properties and annotations.</p></aside>
</div>
<script>
const DATA = __DATA__;
const KIND_COLORS={Collection:'#4e79a7',Activity:'#f28e2b',Observable:'#59a14f',Agent:'#e15759',Instrument:'#b07aa1',Recipe:'#edc948',Resource:'#76b7b2',Role:'#ff9da7',Selector:'#9c755f',Missing:'#bab0ac'};
const NS='http://www.w3.org/2000/svg';
const svg=document.getElementById('graph');
const viewport=document.getElementById('viewport');
const r0=svg.getBoundingClientRect();
const W=r0.width||900, H=r0.height||600;
const nodes=DATA.nodes.map((n,i)=>{const a=2*Math.PI*i/Math.max(1,DATA.nodes.length);return Object.assign({},n,{x:W/2+Math.cos(a)*Math.min(W,H)/3,y:H/2+Math.sin(a)*Math.min(W,H)/3,vx:0,vy:0,fixed:false});});
const byId=new Map(nodes.map(n=>[n.id,n]));
const edges=DATA.edges.map(e=>({source:byId.get(e.source),target:byId.get(e.target),predicate:e.predicate})).filter(e=>e.source&&e.target);
document.getElementById('counts').textContent=nodes.length+' nodes · '+edges.length+' edges';
const legend=document.getElementById('legend');
[...new Set(nodes.map(n=>n.kind))].sort().forEach(k=>{const d=document.createElement('div');const s=document.createElement('span');s.style.background=KIND_COLORS[k]||'#888';d.appendChild(s);d.appendChild(document.createTextNode(k));legend.appendChild(d);});
const edgeEls=edges.map(e=>{const line=document.createElementNS(NS,'line');line.setAttribute('class','edge');viewport.appendChild(line);const lbl=document.createElementNS(NS,'text');lbl.setAttribute('class','elabel');lbl.textContent=e.predicate;viewport.appendChild(lbl);return{e,line,lbl};});
const nodeEls=nodes.map(n=>{const g=document.createElementNS(NS,'g');g.setAttribute('class','node');const c=document.createElementNS(NS,'circle');c.setAttribute('r','10');c.setAttribute('fill',KIND_COLORS[n.kind]||'#888');g.appendChild(c);const t=document.createElementNS(NS,'text');t.setAttribute('class','nlabel');t.setAttribute('x','13');t.setAttribute('y','4');t.textContent=n.label;g.appendChild(t);viewport.appendChild(g);g.addEventListener('mousedown',ev=>startDrag(ev,n));return{n,g};});
let alpha=1;
function step(){
 for(let i=0;i<nodes.length;i++)for(let j=i+1;j<nodes.length;j++){const a=nodes[i],b=nodes[j];let dx=a.x-b.x,dy=a.y-b.y,d2=dx*dx+dy*dy;if(d2<0.01){d2=0.01;dx=Math.random()-0.5;dy=Math.random()-0.5;}const d=Math.sqrt(d2),f=7000/d2,fx=dx/d*f,fy=dy/d*f;a.vx+=fx;a.vy+=fy;b.vx-=fx;b.vy-=fy;}
 edges.forEach(e=>{const dx=e.target.x-e.source.x,dy=e.target.y-e.source.y,d=Math.sqrt(dx*dx+dy*dy)||0.01,f=(d-140)*0.03,fx=dx/d*f,fy=dy/d*f;e.source.vx+=fx;e.source.vy+=fy;e.target.vx-=fx;e.target.vy-=fy;});
 nodes.forEach(n=>{n.vx+=(W/2-n.x)*0.003;n.vy+=(H/2-n.y)*0.003;});
 nodes.forEach(n=>{if(n.fixed)return;const sp=Math.hypot(n.vx,n.vy),cap=30;if(sp>cap){n.vx=n.vx/sp*cap;n.vy=n.vy/sp*cap;}n.x+=n.vx*alpha;n.y+=n.vy*alpha;n.vx*=0.82;n.vy*=0.82;});
 alpha*=0.985;
}
function render(){
 edgeEls.forEach(({e,line,lbl})=>{line.setAttribute('x1',e.source.x);line.setAttribute('y1',e.source.y);line.setAttribute('x2',e.target.x);line.setAttribute('y2',e.target.y);lbl.setAttribute('x',(e.source.x+e.target.x)/2);lbl.setAttribute('y',(e.source.y+e.target.y)/2);});
 nodeEls.forEach(({n,g})=>g.setAttribute('transform','translate('+n.x+','+n.y+')'));
}
function tick(){if(alpha>0.02){step();step();}render();requestAnimationFrame(tick);}
tick();
let tx=0,ty=0,scale=1;
function applyView(){viewport.setAttribute('transform','translate('+tx+','+ty+') scale('+scale+')');}
svg.addEventListener('wheel',ev=>{ev.preventDefault();const r=svg.getBoundingClientRect(),mx=ev.clientX-r.left,my=ev.clientY-r.top,f=ev.deltaY<0?1.1:1/1.1;tx=mx-(mx-tx)*f;ty=my-(my-ty)*f;scale*=f;applyView();},{passive:false});
document.getElementById('reset').addEventListener('click',()=>{tx=0;ty=0;scale=1;applyView();});
let panning=false,px=0,py=0,drag=null;
svg.addEventListener('mousedown',ev=>{if(ev.target===svg||ev.target===viewport){panning=true;px=ev.clientX;py=ev.clientY;}});
window.addEventListener('mousemove',ev=>{
 if(drag){const r=svg.getBoundingClientRect();drag.x=(ev.clientX-r.left-tx)/scale;drag.y=(ev.clientY-r.top-ty)/scale;alpha=Math.max(alpha,0.25);}
 else if(panning){tx+=ev.clientX-px;ty+=ev.clientY-py;px=ev.clientX;py=ev.clientY;applyView();}
});
window.addEventListener('mouseup',()=>{panning=false;if(drag){drag.fixed=false;drag=null;}});
function startDrag(ev,n){ev.stopPropagation();ev.preventDefault();drag=n;n.fixed=true;select(n);}
const panel=document.getElementById('panel');
function addRow(tbl,k,v){const tr=document.createElement('tr'),a=document.createElement('td'),b=document.createElement('td');a.className='k';a.textContent=k;b.textContent=v;tr.appendChild(a);tr.appendChild(b);tbl.appendChild(tr);}
function section(title,pairs){if(!pairs||!pairs.length)return null;const frag=document.createDocumentFragment(),h=document.createElement('h3');h.textContent=title+' ('+pairs.length+')';const tbl=document.createElement('table');pairs.forEach(p=>addRow(tbl,p[0],p[1]));frag.appendChild(h);frag.appendChild(tbl);return frag;}
function select(n){
 nodeEls.forEach(({n:m,g})=>g.classList.toggle('sel',m===n));
 panel.innerHTML='';
 const h=document.createElement('h2');h.textContent=n.label;panel.appendChild(h);
 const b=document.createElement('div');b.className='badge';b.textContent=n.kind;b.style.background=KIND_COLORS[n.kind]||'#888';panel.appendChild(b);
 const meta=document.createElement('table');addRow(meta,'id',n.id);if(n.dtypes)addRow(meta,'dtypes',n.dtypes);panel.appendChild(meta);
 const ps=section('Properties',n.props);if(ps)panel.appendChild(ps);
 const an=section('Annotations',n.annotations);if(an)panel.appendChild(an);
}
</script>
</body>
</html>
"""

    /// The self-contained interactive HTML page for `ir` as a string.
    let toString (ir: ArcIR) = template.Replace("__DATA__", dataJson ir)

    /// Write the self-contained interactive HTML page for `ir` to `path` (UTF-8, no BOM).
    let writeFile (path: string) (ir: ArcIR) =
        use writer = new StreamWriter(path, false, UTF8Encoding(false))
        writer.Write(toString ir)
