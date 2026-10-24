namespace TheGarden.Core;

public readonly record struct TerrainFace(int A, int B, int C);
public readonly record struct SurfaceLocation(int Face, double A, double B, double C);
public readonly record struct TerrainSample(double ElevationMeters, UnitVector TangentGradient,
    UnitVector SurfaceNormal, int Face);

/// <summary>Immutable, closed, oriented spherical triangle surface. Elevation lives at its vertices.
/// Great-circle edges and projective-linear heights use the same contract as vector_terrain.py.</summary>
public sealed class TerrainGeometry
{
    private static readonly Lazy<TerrainGeometry>[] Spheres=Enumerable.Range(0,9)
        .Select(level=>new Lazy<TerrainGeometry>(()=>BuildSphere(level))).ToArray();
    // long.GetHashCode XORs the two vertex indices: adjacent mesh indices then
    // collide heavily. Mix both indices before hashing shared edges.
    private sealed class EdgeComparer : IEqualityComparer<long>
    {
        public static readonly EdgeComparer Instance=new();
        public bool Equals(long a,long b) => a==b;
        public int GetHashCode(long edge)
        {
            var x=(ulong)edge; x^=x>>30; x*=0xbf58476d1ce4e5b9UL;
            x^=x>>27; x*=0x94d049bb133111ebUL; x^=x>>31;
            return (int)(x^(x>>32));
        }
    }
    public const int MaxVertices = 2_000_000;
    public const int MaxFaces = 4_000_000;
    private readonly UnitVector[] vertices;
    private readonly double[] heightWeights;
    private readonly TerrainFace[] faces;
    private readonly FaceData[] data;
    private readonly int[] order;
    private readonly Node root;
    private readonly Lazy<int[][]> neighbors;
    public int VertexCount => vertices.Length;
    public int FaceCount => faces.Length;
    public UnitVector Vertex(int index) => vertices[index];
    public double HeightWeight(int index) => heightWeights[index];
    public TerrainFace Face(int index) => faces[index];
    public double SolidAngle(int index) => data[index].Area;
    internal int[] Neighbors(int index) => neighbors.Value[index];
    private readonly record struct FaceData(UnitVector U, UnitVector V, UnitVector W,
        UnitVector Center, double Radius, double Area, Bounds Bounds);
    private readonly record struct Bounds(UnitVector Min, UnitVector Max)
    {
        public bool Contains(UnitVector p) => p.X >= Min.X - 1e-11 && p.X <= Max.X + 1e-11 &&
            p.Y >= Min.Y - 1e-11 && p.Y <= Max.Y + 1e-11 && p.Z >= Min.Z - 1e-11 && p.Z <= Max.Z + 1e-11;
        public static Bounds Union(Bounds a, Bounds b) => new(
            new(Math.Min(a.Min.X,b.Min.X),Math.Min(a.Min.Y,b.Min.Y),Math.Min(a.Min.Z,b.Min.Z)),
            new(Math.Max(a.Max.X,b.Max.X),Math.Max(a.Max.Y,b.Max.Y),Math.Max(a.Max.Z,b.Max.Z)));
    }
    private sealed record Node(Bounds Bounds, int Start, int Count, Node? Left, Node? Right);

    public TerrainGeometry(IEnumerable<UnitVector> points, IEnumerable<TerrainFace> triangles, IEnumerable<double>? interpolationWeights=null)
        : this(points.ToArray(), triangles.ToArray(), interpolationWeights?.ToArray(), checkEdges: true) { }
    // checkEdges: false only for Refine, whose edge splits keep a closed oriented surface by construction
    // (the shared-edge count is the costly part of the check: ~half of the constructor on a 130k-face mesh).
    private TerrainGeometry(UnitVector[] points, TerrainFace[] triangles, double[]? interpolationWeights, bool checkEdges)
    {
        vertices = points; faces = triangles;
        heightWeights=interpolationWeights ?? Enumerable.Repeat(1.0,vertices.Length).ToArray();
        if(heightWeights.Length!=vertices.Length || heightWeights.Any(q=>!double.IsFinite(q) || q<1e-6 || q>1e6))
            throw new ArgumentException("지형 보간 가중치가 올바르지 않습니다.");
        if (vertices.Length < 4 || vertices.Length > MaxVertices || faces.Length < 4 || faces.Length > MaxFaces)
            throw new ArgumentException("벡터 지형의 꼭짓점/면 개수가 올바르지 않습니다.");
        foreach (var p in vertices)
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z) ||
                Math.Abs(UnitVector.Dot(p,p)-1) > 1e-10)
                throw new ArgumentException("지형 꼭짓점은 유한한 구면 단위 벡터여야 합니다.");
        data = new FaceData[faces.Length];
        var bad = 0;   // first problem found: 1 connectivity, 2 flipped or degenerate, 3 too large
        Parallel.For(0, faces.Length, i =>
        {
            var f = faces[i];
            if ((uint)f.A >= vertices.Length || (uint)f.B >= vertices.Length || (uint)f.C >= vertices.Length ||
                f.A == f.B || f.B == f.C || f.C == f.A)
            { Interlocked.CompareExchange(ref bad,1,0); return; }
            var a=vertices[f.A]; var b=vertices[f.B]; var c=vertices[f.C];
            var det=UnitVector.Dot(a,UnitVector.Cross(b,c));
            if (det <= 1e-15) { Interlocked.CompareExchange(ref bad,2,0); return; }
            var center=(a+b+c).Normalized();
            var radius=Math.Acos(Math.Clamp(Math.Min(UnitVector.Dot(center,a),
                Math.Min(UnitVector.Dot(center,b),UnitVector.Dot(center,c))),-1,1));
            if (radius >= Math.PI/2 - 1e-10) { Interlocked.CompareExchange(ref bad,3,0); return; }
            var chord=2*Math.Sin(radius/2);
            var d=new UnitVector(chord,chord,chord);
            data[i]=new(UnitVector.Cross(b,c)*(1/det),UnitVector.Cross(c,a)*(1/det),
                UnitVector.Cross(a,b)*(1/det),center,radius,TriangleArea(a,b,c),new(center-d,center+d));
        });
        if (bad != 0) throw new ArgumentException(bad == 1 ? "지형 면의 꼭짓점 연결이 올바르지 않습니다."
            : bad == 2 ? "지형 면이 뒤집혔거나 퇴화했습니다." : "하나의 지형 면이 너무 큽니다.");
        var edges = new Dictionary<long, (int Count, int Direction)>(EdgeComparer.Instance);
        var used = new bool[vertices.Length];
        double total = 0;
        for (var i=0; i<faces.Length; i++)
        {
            var f = faces[i];
            used[f.A]=used[f.B]=used[f.C]=true; total+=data[i].Area;
            if (checkEdges) { AddEdge(f.A,f.B); AddEdge(f.B,f.C); AddEdge(f.C,f.A); }
        }
        if (used.Any(x=>!x) || edges.Values.Any(x=>x.Count!=2 || x.Direction!=0) ||
            Math.Abs(total-4*Math.PI)>1e-8)
            throw new ArgumentException("지형은 틈 없이 구면 한 개를 덮어야 합니다.");
        order=Enumerable.Range(0,faces.Length).ToArray();
        root=Build(0,order.Length);
        neighbors=new(BuildNeighbors);
        void AddEdge(int a,int b)
        {
            var key=EdgeKey(a,b); edges.TryGetValue(key,out var value);
            edges[key]=(value.Count+1,value.Direction+(a<b?1:-1));
        }
    }
    public static double TriangleArea(UnitVector a,UnitVector b,UnitVector c) =>
        2*Math.Atan2(Math.Abs(UnitVector.Dot(a,UnitVector.Cross(b,c))),
            1+UnitVector.Dot(a,b)+UnitVector.Dot(b,c)+UnitVector.Dot(c,a));
    internal static long EdgeKey(int a,int b) => ((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
    private Node Build(int start,int count)
    {
        var bounds=data[order[start]].Bounds;
        for(var i=start+1;i<start+count;i++) bounds=Bounds.Union(bounds,data[order[i]].Bounds);
        if(count<=8) return new(bounds,start,count,null,null);
        var extent=bounds.Max-bounds.Min;
        var axis=extent.X>=extent.Y && extent.X>=extent.Z?0:extent.Y>=extent.Z?1:2;
        double Coordinate(int i) { var p=data[i].Center; return axis==0?p.X:axis==1?p.Y:p.Z; }
        var midpoint=axis==0?(bounds.Min.X+bounds.Max.X)/2:axis==1?(bounds.Min.Y+bounds.Max.Y)/2:(bounds.Min.Z+bounds.Max.Z)/2;
        var left=start; var right=start+count-1;
        while(left<=right)
        {
            if(Coordinate(order[left])<midpoint) left++;
            else { (order[left],order[right])=(order[right],order[left]); right--; }
        }
        var half=left-start;
        if(half==0 || half==count) half=count/2;
        return new(bounds,start,count,Build(start,half),Build(start+half,count-half));
    }
    public SurfaceLocation Locate(UnitVector direction)
    {
        var p=direction.Normalized();
        return Find(root,p) ?? throw new InvalidOperationException("구면 지형에서 위치를 찾지 못했습니다.");
    }
    private SurfaceLocation? Find(Node node,UnitVector p)
    {
        if(!node.Bounds.Contains(p)) return null;
        if(node.Left is not null) return Find(node.Left,p) ?? Find(node.Right!,p);
        for(var i=node.Start;i<node.Start+node.Count;i++)
        {
            var face=order[i]; var d=data[face];
            var a=UnitVector.Dot(d.U,p); var b=UnitVector.Dot(d.V,p); var c=UnitVector.Dot(d.W,p);
            var sum=a+b+c;
            if(sum>0 && a>=-1e-10 && b>=-1e-10 && c>=-1e-10)
            {
                var f=faces[face];
                a=Math.Max(0,a)*heightWeights[f.A]; b=Math.Max(0,b)*heightWeights[f.B]; c=Math.Max(0,c)*heightWeights[f.C]; sum=a+b+c;
                return new(face,a/sum,b/sum,c/sum);
            }
        }
        return null;
    }
    internal UnitVector Gradient(int face, UnitVector p, double height, float ha,float hb,float hc,double radius)
    {
        var d=data[face]; var f=faces[face];
        var u=d.U*heightWeights[f.A]; var v=d.V*heightWeights[f.B]; var w=d.W*heightWeights[f.C]; var b=u+v+w;
        var gradient=(u*ha+v*hb+w*hc-b*height)*(1/UnitVector.Dot(b,p));
        return (gradient-p*UnitVector.Dot(gradient,p))*(1/radius);
    }
    private int[][] BuildNeighbors()
    {
        var lists=Enumerable.Range(0,vertices.Length).Select(_=>new List<int>(6)).ToArray();
        foreach(var f in faces)
        { lists[f.A].AddRange([f.B,f.C]); lists[f.B].AddRange([f.A,f.C]); lists[f.C].AddRange([f.A,f.B]); }
        return lists.Select(x=>x.Distinct().Order().ToArray()).ToArray();
    }
    public static TerrainGeometry CreateSphere(int level)
    {
        if(level<0 || level>8) throw new ArgumentOutOfRangeException(nameof(level));
        return Spheres[level].Value;
    }
    private static TerrainGeometry BuildSphere(int level)
    {
        var p=new List<UnitVector> { new(1,0,0),new(0,1,0),new(0,0,1),new(-1,0,0),new(0,-1,0),new(0,0,-1) };
        var f=new List<TerrainFace>();
        foreach(var x in new[]{0,3}) foreach(var y in new[]{1,4}) foreach(var z in new[]{2,5})
            f.Add(UnitVector.Dot(p[x],UnitVector.Cross(p[y],p[z]))>0?new(x,y,z):new(x,z,y));
        for(var l=0;l<level;l++)
        {
            var mids=new Dictionary<long,int>(EdgeComparer.Instance); var next=new List<TerrainFace>(f.Count*4);
            int Mid(int a,int b)
            {
                var key=EdgeKey(a,b);
                if(!mids.TryGetValue(key,out var m)) { m=p.Count; p.Add((p[a]+p[b]).Normalized()); mids[key]=m; }
                return m;
            }
            foreach(var t in f)
            {
                var ab=Mid(t.A,t.B); var bc=Mid(t.B,t.C); var ca=Mid(t.C,t.A);
                next.AddRange([new(t.A,ab,ca),new(ab,t.B,bc),new(ca,bc,t.C),new(ab,bc,ca)]);
            }
            f=next;
        }
        return new(p,f);
    }
    // Split marked edges in BOTH incident faces. New heights lie on the old surface.
    // FaceMap: new index of each old face that was kept whole (-1 if split); kept faces keep their
    // vertices and weights, so a location inside one stays valid under its new index.
    internal (TerrainGeometry Geometry,float[] Heights,int[]? FaceMap) Refine(UnitVector center,double angularRadius,float[] heights) =>
        Refine([center],angularRadius,heights);
    // Several brush stamps at once (one stroke segment): one rebuild instead of one per stamp.
    // All split levels run on plain lists and the geometry (face data, search tree) is built once at the end:
    // a level scans only the faces the previous level made (the others' edges did not change).
    internal (TerrainGeometry Geometry,float[] Heights,int[]? FaceMap) Refine(IReadOnlyList<UnitVector> centers,double angularRadius,float[] heights)
    {
        var target=Math.Cos(angularRadius/3);
        List<UnitVector>? p=null; List<float>? h=null; List<double>? q=null;
        IReadOnlyList<TerrainFace> current=faces; List<int>? fresh=null; int[]? map=null;
        for(var level=0;level<20;level++)
        {
            var marked=new HashSet<long>(EdgeComparer.Instance);
            if(fresh is null)
                for(var i=0;i<faces.Length;i++) { if(Near(data[i].Center,data[i].Radius)) MarkFace(faces[i]); }
            else
                foreach(var i in fresh)
                {
                    var f=current[i]; var a=p![f.A]; var b=p[f.B]; var c=p[f.C]; var center=(a+b+c).Normalized();
                    var radius=Math.Acos(Math.Clamp(Math.Min(UnitVector.Dot(center,a),Math.Min(UnitVector.Dot(center,b),UnitVector.Dot(center,c))),-1,1));
                    if(Near(center,radius)) MarkFace(f);
                }
            if(marked.Count==0) break;
            p??=vertices.ToList(); h??=heights.ToList(); q??=heightWeights.ToList();
            if(p.Count+marked.Count>MaxVertices || current.Count+marked.Count*2>MaxFaces)
                throw new InvalidOperationException("지형 세밀도 한도에 도달했습니다. 더 큰 붓을 사용하세요.");
            var mids=new Dictionary<long,int>(marked.Count,EdgeComparer.Instance);
            foreach(var key in marked.Order())
            {
                var a=(int)(key>>32); var b=(int)(uint)key;
                var sum=p[a]+p[b]; var length=Math.Sqrt(UnitVector.Dot(sum,sum));
                mids[key]=p.Count; p.Add(sum*(1/length));
                h.Add((float)((q[a]*h[a]+q[b]*h[b])/(q[a]+q[b])));
                q.Add((q[a]+q[b])/length);
            }
            var next=new List<TerrainFace>(current.Count+marked.Count*2); var step=new int[current.Count]; var born=new List<int>();
            for(var i=0;i<current.Count;i++)
            {
                var f=current[i]; step[i]=-1;
                var ab=mids.GetValueOrDefault(EdgeKey(f.A,f.B),-1);
                var bc=mids.GetValueOrDefault(EdgeKey(f.B,f.C),-1);
                var ca=mids.GetValueOrDefault(EdgeKey(f.C,f.A),-1);
                var mask=(ab>=0?1:0)|(bc>=0?2:0)|(ca>=0?4:0);
                if(mask==0) { step[i]=next.Count; next.Add(f); continue; }
                var first=next.Count;
                switch(mask)
                {
                    case 1: One(f.A,f.B,f.C,ab); break;
                    case 2: One(f.B,f.C,f.A,bc); break;
                    case 4: One(f.C,f.A,f.B,ca); break;
                    case 3: Two(f.A,f.B,f.C,ab,bc); break;
                    case 6: Two(f.B,f.C,f.A,bc,ca); break;
                    case 5: Two(f.C,f.A,f.B,ca,ab); break;
                    case 7: next.AddRange([new(f.A,ab,ca),new(ab,f.B,bc),new(ca,bc,f.C),new(ab,bc,ca)]); break;
                }
                for(var k=first;k<next.Count;k++) born.Add(k);
            }
            map=map is null ? step : Array.ConvertAll(map,f=>f<0?-1:step[f]);
            current=next; fresh=born;
            void One(int a,int b,int c,int m) => next.AddRange([new(a,m,c),new(m,b,c)]);
            void Two(int a,int b,int c,int ab,int bc) => next.AddRange([new(b,bc,ab),new(a,ab,c),new(ab,bc,c)]);
            void MarkFace(TerrainFace f) { Mark(f.A,f.B); Mark(f.B,f.C); Mark(f.C,f.A); }
            void Mark(int a,int b)
            { if(UnitVector.Dot(p?[a]??vertices[a],p?[b]??vertices[b])<target) marked.Add(EdgeKey(a,b)); }
        }
        if(map is null) return (this,heights,null);
        return(new(p!.ToArray(),current.ToArray(),q!.ToArray(),checkEdges:false),h!.ToArray(),map);
        bool Near(UnitVector center,double radius)
        {
            var cos=Math.Cos(Math.Min(Math.PI,angularRadius+radius));
            foreach(var c in centers) if(UnitVector.Dot(c,center)>=cos) return true;
            return false;
        }
    }
}
