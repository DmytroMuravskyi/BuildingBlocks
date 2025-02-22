using Elements;
using Elements.Geometry;
using Elements.Geometry.Solids;
using System.Collections.Generic;
using System.Linq;

namespace RoofFunction
{
    public static class RoofFunction
    {
        /// <summary>
        /// The RoofFunction function.
        /// </summary>
        /// <param name="model">The input model.</param>
        /// <param name="input">The arguments to the execution.</param>
        /// <returns>A RoofFunctionOutputs instance containing computed results and the model with any new elements.</returns>
        public static RoofFunctionOutputs Execute(Dictionary<string, Model> inputModels, RoofFunctionInputs input)
        {
            var output = new RoofFunctionOutputs();
            Roof.RoofMaterial.Color = input.RoofColor;
            var hasFootprints = inputModels.TryGetValue("Masterplan", out var masterplanModel);
            var hasEnvelopes = inputModels.TryGetValue("Envelope", out var envelopeModel);
            var hasLevels = inputModels.TryGetValue("Levels", out var levelsModel);
            if (!hasEnvelopes && !hasFootprints && !hasLevels)
            {
                output.Warnings.Add("There's nothing in the model from which to create a roof. Please add a footprint, envelope, or levels.");
            }
            if (hasFootprints)
            {
                var footprints = masterplanModel.AllElementsOfType<Footprint>();
                CreateRoofsFromElements(footprints, input, output);
            }
            else if (hasEnvelopes)
            {
                var envelopes = envelopeModel.AllElementsOfType<Envelope>();
                CreateRoofsFromElements(envelopes, input, output);
            }
            else if (hasLevels)
            {
                var levelVolumes = levelsModel?.AllElementsOfType<LevelVolume>();
                var projectedProfiles = Profile.UnionAll(levelVolumes.Select(lv => lv.Profile));
                foreach (var union in projectedProfiles)
                {
                    var profilesInUnion = levelVolumes
                      .Where(lv => union.Contains(lv.Profile.Perimeter.PointInternal()))
                      .GroupBy(lv => lv.Transform.Origin.Z)
                      .OrderBy(grp => grp.Key);
                    var keys = profilesInUnion.Select(g => g.Key).Reverse();
                    var dict = profilesInUnion.ToDictionary(grp => grp.Key, grp => grp);
                    List<Profile> previousProfiles = null;
                    // counting down from top
                    foreach (var elevation in keys)
                    {
                        var allLvs = dict[elevation];
                        if (previousProfiles == null)
                        {
                            previousProfiles = new List<Profile>();
                            foreach (var lv in allLvs)
                            {
                                var lvProfile = lv.Profile;
                                previousProfiles.Add(lvProfile);
                                var roof = new Roof(lv.Profile, input.RoofThickness, lv.Transform.Concatenated(new Transform(0, 0, lv.Height)));
                                if (lv.AdditionalProperties.TryGetValue("Envelope", out var envId))
                                {
                                    roof.AdditionalProperties.Add("Envelope", envId);
                                }
                                if (lv.AdditionalProperties.TryGetValue("Footprint", out var fpId))
                                {
                                    roof.AdditionalProperties.Add("Footprint", fpId);
                                }
                                output.Model.AddElement(roof);
                            }
                        }
                        else
                        {
                            var thisLevelprofiles = allLvs.Select(lv => lv.Profile);
                            var difference = Profile.Difference(thisLevelprofiles, previousProfiles);
                            if (difference != null && difference.Count > 0)
                            {
                                foreach (var profile in difference.Where(d => d.Area() > 5))
                                {
                                    var roof = new Roof(profile, input.RoofThickness, allLvs.First().Transform.Concatenated(new Transform(0, 0, allLvs.First().Height)));
                                    if (allLvs.First().AdditionalProperties.TryGetValue("Envelope", out var envId))
                                    {
                                        roof.AdditionalProperties.Add("Envelope", envId);
                                    }
                                    if (allLvs.First().AdditionalProperties.TryGetValue("Footprint", out var fpId))
                                    {
                                        roof.AdditionalProperties.Add("Footprint", fpId);
                                    }
                                    output.Model.AddElement(roof);
                                }
                            }
                            previousProfiles = thisLevelprofiles.ToList();
                        }
                    }
                }
            }
            return output;
        }

        private static void CreateRoofsFromElements<T>(IEnumerable<T> elements, RoofFunctionInputs input, RoofFunctionOutputs output) where T : GeometricElement
        {
            foreach (var element in elements)
            {
                var solids = element.Representation.SolidOperations.Where(so => !so.IsVoid).Select(so => so.Solid);
                var roofFaces = solids.SelectMany(s => s.Faces.Where(f => f.Value.Outer.ToPolygon().Normal().Dot(Vector3.ZAxis) > 0.7).Select(f => f.Value));
                // Certain functions, like sketch masterplan, create their solids as a collection of stacked volumes with a very slight gap between them.
                // This is a workaround for bugs in the unions created by solid operations containing multiple solids.
                // We don't want to create "roof" elements in those gaps, so we look for cases where the potential roof face is very close to
                // another face, and ignore those. 
                var bottomFaceCentroids = solids.SelectMany(s => s.Faces.Where(f => f.Value.Outer.ToPolygon().Normal().Dot(Vector3.ZAxis) < -0.7).Select(f => f.Value.Outer.ToPolygon().Centroid()));

                foreach (var roofFace in roofFaces)
                {
                    var pgon = roofFace.Outer.ToPolygon();
                    var centroid = pgon.Centroid();
                    if (bottomFaceCentroids.Any(c => c.DistanceTo(centroid) < 0.5))
                    {
                        continue;
                    }
                    var polygon = roofFace.Outer.ToPolygon();
                    var polygonTransform = polygon.ToTransform();
                    var inverse = polygonTransform.Inverted();
                    var profile = new Profile(polygon.TransformedPolygon(inverse), roofFace.Inner.Select(i => i.ToPolygon().TransformedPolygon(inverse)).ToList());
                    var roof = new Roof(profile, input.RoofThickness, polygonTransform.Concatenated(element.Transform));
                    roof.AdditionalProperties["Envelope"] = element.Id;
                    output.Model.AddElement(roof);
                }
            }
        }
    }
}