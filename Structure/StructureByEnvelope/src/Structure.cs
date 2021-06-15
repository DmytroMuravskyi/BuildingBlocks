using System;
using System.Collections.Generic;
using System.Linq;
using Elements;
using Elements.Geometry;
using Elements.Geometry.Profiles;
using Elements.Spatial;
using Elements.Spatial.CellComplex;

namespace Structure
{
    public static class Structure
    {
        private const string BAYS_MODEL_NAME = "Bays";
        private const string GRIDS_MODEL_NAME = "Grids";
        private const string LEVELS_MODEL_NAME = "Levels";
        private const double DEFAULT_U = 5.0;
        private const double DEFAULT_V = 7.0;
        private static List<Material> _lengthGradient = new List<Material>(){
            new Material(Colors.Green, 0.0f, 0.0f, false, null, false, Guid.NewGuid(), "Gradient 1"),
            new Material(Colors.Cyan, 0.0f, 0.0f, false, null, false, Guid.NewGuid(), "Gradient 2"),
            new Material(Colors.Lime, 0.0f, 0.0f, false, null, false, Guid.NewGuid(), "Gradient 3"),
            new Material(Colors.Yellow, 0.0f, 0.0f, false, null, false, Guid.NewGuid(), "Gradient 4"),
            new Material(Colors.Orange, 0.0f, 0.0f, false, null, false, Guid.NewGuid(), "Gradient 5"),
            new Material(Colors.Red, 0.0f, 0.0f, false, null, false, Guid.NewGuid(), "Gradient 6"),
        };

        private static double _longestGridSpan = 0.0;

        /// <summary>
		/// The Structure function.
		/// </summary>
		/// <param name="model">The model. 
		/// Add elements to the model to have them persisted.</param>
		/// <param name="input">The arguments to the execution.</param>
		/// <returns>A StructureOutputs instance containing computed results.</returns>
		public static StructureOutputs Execute(Dictionary<string, Model> models, StructureInputs input)
        {
            var model = new Model();

            CellComplex cellComplex = null;
            Line longestEdge = null;

            if (models.ContainsKey(BAYS_MODEL_NAME))
            {
                var cellsModel = models[BAYS_MODEL_NAME];
                cellComplex = cellsModel.AllElementsOfType<CellComplex>().First();
            }
            else
            {
                // Create a cell complex with some defaults.
                if (!models.ContainsKey(LEVELS_MODEL_NAME))
                {
                    throw new Exception("If Bays are not supplied Levels are required.");
                }

                var levels = models[LEVELS_MODEL_NAME];
                var levelVolumes = levels.AllElementsOfType<LevelVolume>().ToList();
                if (levelVolumes.Count == 0)
                {
                    throw new Exception("No LevelVolumes found in your Levels model. Please use a level function that generates LevelVolumes, such as Simple Levels by Envelope");
                }

                // Replicate the old behavior by creating a 
                // grid using the envelope's first level base polygon's longest
                // edge as the U axis and its perpendicular as the
                // V axis.

                var firstLevel = levelVolumes[0];
                var firstLevelPerimeter = firstLevel.Profile.Perimeter;
                longestEdge = firstLevelPerimeter.Segments().OrderBy(s => s.Length()).Last();
                var cellComplexOrigin = firstLevel.Profile.Perimeter.Vertices[0];
                var maxDistance = double.MinValue;
                foreach (var levelV in firstLevelPerimeter.Vertices)
                {
                    var d = levelV.DistanceTo(longestEdge);
                    if (d > maxDistance)
                    {
                        maxDistance = d;
                    }
                }

                var uGrid = new Grid1d(new Line(cellComplexOrigin, cellComplexOrigin + longestEdge.Direction() * longestEdge.Length()));
                uGrid.DivideByFixedLength(5);

                var t = longestEdge.TransformAt(0.5);
                var perpDirection = t.XAxis;
                var c = firstLevelPerimeter.Centroid();
                var dirToCentroid = (t.Origin - c).Unitized();
                var dot = dirToCentroid.Dot(perpDirection);
                var perpendicularEdge = new Line(cellComplexOrigin, cellComplexOrigin + (dot > 0.0 ? perpDirection : perpDirection.Negate()) * maxDistance);
                var vGrid = new Grid1d(perpendicularEdge);
                vGrid.DivideByFixedLength(7);
                var grid = new Grid2d(uGrid, vGrid);

                var u = grid.U;
                var v = grid.V;

                cellComplex = new CellComplex(Guid.NewGuid(), "Temporary Cell Complex");

                // Draw level volumes from each level down.
                for (var i = 1; i < levelVolumes.Count; i++)
                {
                    var levelVolume = levelVolumes.ElementAt(i);
                    var perimeter = levelVolume.Profile.Perimeter.Offset(-0.5)[0];
                    var g2d = new Grid2d(perimeter, grid.U, grid.V);
                    var levelElevation = levelVolume.Transform.Origin.Z;
                    var lastLevelVolume = levelVolumes.ElementAt(i - 1);
                    foreach (var cell in g2d.GetCells())
                    {
                        foreach (var crv in cell.GetTrimmedCellGeometry())
                        {
                            cellComplex.AddCell((Polygon)crv, lastLevelVolume.Height, levelElevation - lastLevelVolume.Height, g2d.U, g2d.V);
                            if (i == levelVolumes.Count - 1)
                            {
                                cellComplex.AddCell((Polygon)crv, levelVolume.Height, levelElevation, g2d.U, g2d.V);
                            }
                        }
                    }
                }
            }

            Vector3 primaryDirection;
            if (models.ContainsKey(GRIDS_MODEL_NAME))
            {
                var gridsModel = models[GRIDS_MODEL_NAME];
                var gridLines = gridsModel.AllElementsOfType<GridLine>();
                primaryDirection = gridLines.ElementAt(0).Geometry.Segments()[0].Direction();
            }
            else
            {
                // Define the primary direction from the longest edge of the site.
                primaryDirection = longestEdge.Direction();
            }

            var structureMaterial = new Material("Steel", Colors.Gray, 0.5, 0.3);
            model.AddElement(structureMaterial);

            var wideFlangeFactory = new WideFlangeProfileFactory();
            var columnProfile = wideFlangeFactory.GetProfileByName(input.ColumnType.ToString());
            var colProfileBounds = columnProfile.Perimeter.Bounds();
            var colProfileDepth = colProfileBounds.Max.Y - colProfileBounds.Min.Y;
            var girderProfile = wideFlangeFactory.GetProfileByName(input.GirderType.ToString());
            var girdProfileBounds = columnProfile.Perimeter.Bounds();
            var girderProfileDepth = girdProfileBounds.Max.Y - girdProfileBounds.Min.Y;
            var beamProfile = wideFlangeFactory.GetProfileByName(input.BeamType.ToString());
            var beamProfileBounds = beamProfile.Perimeter.Bounds();
            var beamProfileDepth = beamProfileBounds.Max.Y - beamProfileBounds.Min.Y;

            var edges = cellComplex.GetEdges();
            var lowestTierSet = false;
            var lowestTierElevation = double.MaxValue;

            // Order edges from lowest to highest.
            foreach (var edge in edges.OrderBy(e =>
                Math.Min(cellComplex.GetVertex(e.StartVertexId).Value.Z, cellComplex.GetVertex(e.EndVertexId).Value.Z)
            ))
            {
                var isExternal = edge.GetFaces().Count < 4;

                var start = cellComplex.GetVertex(edge.StartVertexId).Value;
                var end = cellComplex.GetVertex(edge.EndVertexId).Value;

                var l = new Line(start - new Vector3(0, 0, input.SlabThickness + girderProfileDepth / 2), end - new Vector3(0, 0, input.SlabThickness + girderProfileDepth / 2));
                StructuralFraming framing = null;

                if (l.IsVertical())
                {
                    if (!input.InsertColumnsAtExternalEdges && isExternal)
                    {
                        continue;
                    }
                    var origin = start.IsLowerThan(end) ? start : end;
                    var rotation = Vector3.XAxis.AngleTo(primaryDirection);
                    framing = new Column(origin, l.Length(), columnProfile, structureMaterial, rotation: rotation);
                }
                else
                {
                    if (!lowestTierSet)
                    {
                        lowestTierElevation = l.Start.Z;
                        lowestTierSet = true;
                    }

                    if (input.CreateBeamsOnFirstLevel)
                    {
                        framing = new Beam(l, girderProfile, structureMaterial);
                    }
                    else
                    {
                        if (l.Start.Z > lowestTierElevation)
                        {
                            framing = new Beam(l, girderProfile, structureMaterial);
                        }
                    }
                }

                if (framing != null)
                {
                    model.AddElement(framing, false);
                }
            }

            foreach (var cell in cellComplex.GetCells())
            {
                var topFace = cell.GetTopFace();
                var p = topFace.GetGeometry();
                var longestCellEdge = p.Segments().OrderBy(s => s.Length()).Last();
                var d = longestCellEdge.Direction();
                var beamGrid = new Grid1d(longestCellEdge);
                beamGrid.DivideByFixedLength(input.BeamSpacing, FixedDivisionMode.RemainderAtBothEnds);
                var segments = p.Segments();
                foreach (var pt in beamGrid.GetCellSeparators())
                {
                    // Skip beams that would be too close to the ends 
                    // to be useful.
                    if (pt.DistanceTo(longestCellEdge.Start) < 1 || pt.DistanceTo(longestCellEdge.End) < 1)
                    {
                        continue;
                    }
                    var t = new Transform(pt, d, Vector3.ZAxis);
                    var r = new Ray(t.Origin, t.YAxis);
                    foreach (var s in segments)
                    {
                        if (s == longestCellEdge)
                        {
                            continue;
                        }

                        if (r.Intersects(s, out Vector3 xsect))
                        {
                            if (t.Origin.DistanceTo(xsect) < 1)
                            {
                                continue;
                            }
                            var l = new Line(t.Origin - new Vector3(0, 0, input.SlabThickness + beamProfileDepth / 2), xsect - new Vector3(0, 0, input.SlabThickness + beamProfileDepth / 2));
                            var beam = new Beam(l, beamProfile, structureMaterial);
                            model.AddElement(beam, false);
                        }
                    }
                    // model.AddElements(t.ToModelCurves());
                }
            }

            var output = new StructureOutputs(_longestGridSpan);
            output.Model = model;
            return output;
        }
    }
}

internal static class Vector3Extensions
{
    public static bool IsDirectlyUnder(this Vector3 a, Vector3 b)
    {
        return a.Z > b.Z && a.X.ApproximatelyEquals(b.X) && a.Y.ApproximatelyEquals(b.Y);
    }

    public static bool IsHigherThan(this Vector3 a, Vector3 b)
    {
        return a.Z > b.Z;
    }

    public static bool IsLowerThan(this Vector3 a, Vector3 b)
    {
        return a.Z < b.Z;
    }

    public static bool IsVertical(this Line line)
    {
        return line.Start.IsDirectlyUnder(line.End) || line.End.IsDirectlyUnder(line.Start);
    }
}