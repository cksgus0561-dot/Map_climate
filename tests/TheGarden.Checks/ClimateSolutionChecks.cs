using System.Text.Json.Nodes;
using TheGarden.Core.Climate;

internal static class ClimateSolutionChecks
{
    public static void Register(Action<string, Action> check)
    {
        check("Seasonal alternatives retain separate climates, identity and verified state files", () =>
        {
            var path=Path.Combine(AppContext.BaseDirectory,"Fixtures","ClimateAlternatives","solutions.json");
            var catalog=ClimateSolutionCatalog.Read(path);
            if(catalog.Alternatives.Count!=2 || catalog.Alternatives[0].TemperatureC.SequenceEqual(catalog.Alternatives[1].TemperatureC))
                throw new Exception("Separate alternatives were lost");
            if(!catalog.Matches(new string('a',64)) || catalog.Matches(new string('b',64))) throw new Exception("Terrain identity mismatch");
            if(catalog.Alternatives.Any(x=>!x.SyntheticFixture)) throw new Exception("Fixture must not be shown as a real climate");
            foreach(var mutation in new[]{"path","approval","summary","hash"})
            {
                var json=JsonNode.Parse(File.ReadAllText(path))!;var item=json["candidates"]![0]!;
                if(mutation=="path") item["state_file"]="../escape.npz";
                if(mutation=="approval") item["accepted"]=false;
                if(mutation=="summary") item["summary"]!["seasonal_snow_cover_fraction"]![0]=2;
                if(mutation=="hash") item["state_sha256"]=new string('0',64);
                var test=Path.Combine(Path.GetDirectoryName(path)!,"invalid-"+mutation+".json");File.WriteAllText(test,json.ToJsonString());
                bool rejected=false;
                try { ClimateSolutionCatalog.Read(test); } catch(InvalidDataException) { rejected=true; }
                finally { File.Delete(test); }
                if(!rejected) throw new Exception("Invalid "+mutation+" accepted");
            }
        });
    }
}
