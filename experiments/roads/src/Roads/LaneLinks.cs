using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CitySim.Splines;

namespace CitySim.Roads;

/// <summary>Which way a link goes across a junction, seen from the lane coming in (right-hand traffic).</summary>
public enum Move { Straight, Right, Left, UTurn }

/// <summary>The lanes of one arm at a junction's mouth, in plan metres: the lanes coming in and going out (kerbside
/// first), the direction away from the node and from the centre toward the kerb of the lanes coming in.</summary>
public sealed record ArmLanes(int Edge, bool AtStart, Vector2 Mouth, Vector2 Outward, Vector2 Kerb,
    IReadOnlyList<Vector2> In, IReadOnlyList<Vector2> Out, float LaneWidth);

/// <summary>A lane coming in from arm <see cref="From"/> linked to a lane going out of arm <see cref="To"/> (indices
/// into the junction's <see cref="ArmLanes"/> list, lanes kerbside first).</summary>
public readonly record struct LaneLink(int From, int FromLane, int To, int ToLane, Move Move);

/// <summary>What the player set for the links leaving one arm (<see cref="RoadEnd.Links"/>): the links, to arms named
/// by their <see cref="RoadEnd.Tag"/>, and the tags of the arms they were set against. An arm not in
/// <see cref="Known"/> (a road added to the junction later) gets the automatic links.</summary>
public sealed record LinkSet(IReadOnlyList<int> Known, IReadOnlyList<(int FromLane, int ToTag, int ToLane)> Links);

/// <summary>
/// Lane links: which lane coming into a junction may go to which lane going out. The road's rule (<see cref="Auto"/>):
/// straight on from every lane to the lane most in line with it, and into every lane out (so lanes merge where a road
/// narrows and branch where it widens), right turns from the kerbside lane to the kerbside lane, left turns
/// from the inside lane to the inside lane, no U-turns. The Lane Links tool overrides that per junction; the wear tracks
/// and the chevrons (<see cref="RoadVisual"/>) follow whatever the links are.
/// </summary>
public static class LaneLinks
{
    /// <summary>How square two arms must be to count as straight on (cosine).</summary>
    private const float StraightDot = 0.7f;

    public static Move MoveOf(ArmLanes a, ArmLanes b)
    {
        if (ReferenceEquals(a, b)) return Move.UTurn;
        if (Vector2.Dot(-a.Outward, b.Outward) > StraightDot) return Move.Straight;
        return Vector2.Dot(b.Outward, a.Kerb) > 0 ? Move.Right : Move.Left;
    }

    /// <summary>The road's rule for the lanes from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static IEnumerable<(int FromLane, int ToLane)> Auto(ArmLanes a, ArmLanes b)
    {
        if (a.In.Count == 0 || b.Out.Count == 0) yield break;
        switch (MoveOf(a, b))
        {
            case Move.Straight:
                // Each lane on to the lane out most in line with it, then every lane out still unfed from the lane in
                // most in line with it: where lanes end they merge into a neighbour, where lanes start one branches into
                // both, and a road moved sideways (an offset) lines up by where its lanes really are.
                float Across(Vector2 p) => Vector2.Dot(p, a.Kerb);
                int Nearest(Vector2 p, IReadOnlyList<Vector2> lanes) =>
                    Enumerable.Range(0, lanes.Count).MinBy(i => MathF.Abs(Across(lanes[i]) - Across(p)) + i * 1e-4f);
                var fed = new HashSet<int>();
                for (int k = 0; k < a.In.Count; k++)
                {
                    int to = Nearest(a.In[k], b.Out);
                    fed.Add(to);
                    yield return (k, to);
                }
                for (int j = 0; j < b.Out.Count; j++)
                    if (!fed.Contains(j)) yield return (Nearest(b.Out[j], a.In), j);
                break;
            case Move.Right:
                yield return (0, 0);
                break;
            case Move.Left:
                yield return (a.In.Count - 1, b.Out.Count - 1);
                break;
        }
    }

    /// <summary>The links at a junction: what the player set where they set it, the road's rule everywhere else.</summary>
    public static List<LaneLink> Resolve(SplineGraph g, IReadOnlyList<ArmLanes> arms)
    {
        var ends = arms.Select(a => RoadEnd.Of(g.Edge(a.Edge), a.AtStart)).ToList();
        var links = new List<LaneLink>();
        for (int i = 0; i < arms.Count; i++)
        {
            var set = ends[i].Links;
            for (int j = 0; j < arms.Count; j++)
            {
                var (a, b) = (arms[i], arms[j]);
                var move = MoveOf(a, b);
                int tag = ends[j].Tag;
                if (set is not null && tag != 0 && set.Known.Contains(tag))
                {
                    foreach (var (from, to, toLane) in set.Links)
                        if (to == tag && from < a.In.Count && toLane < b.Out.Count) links.Add(new LaneLink(i, from, j, toLane, move));
                }
                else foreach (var (from, to) in Auto(a, b)) links.Add(new LaneLink(i, from, j, to, move));
            }
        }
        return links.Distinct().ToList();
    }

    /// <summary>Whether the player has set any links at the junction.</summary>
    public static bool Edited(SplineGraph g, IReadOnlyList<ArmLanes> arms) =>
        arms.Any(a => RoadEnd.Of(g.Edge(a.Edge), a.AtStart).Links is not null);

    /// <summary>Stores <paramref name="links"/> as the junction's links: every arm gets a tag (once) and every arm its
    /// links out, set against all the arms there now.</summary>
    public static void Set(SplineGraph g, IReadOnlyList<ArmLanes> arms, IEnumerable<LaneLink> links)
    {
        int next = g.Edges.SelectMany(e => new[] { RoadEnd.Of(e, true).Tag, RoadEnd.Of(e, false).Tag }).DefaultIfEmpty(0).Max() + 1;
        var tags = new int[arms.Count];
        for (int i = 0; i < arms.Count; i++)
        {
            var end = RoadEnd.Of(g.Edge(arms[i].Edge), arms[i].AtStart);
            tags[i] = end.Tag != 0 ? end.Tag : next++;
        }
        var all = links.ToList();
        for (int i = 0; i < arms.Count; i++)
        {
            var mine = all.Where(l => l.From == i).Select(l => (l.FromLane, tags[l.To], l.ToLane)).Distinct().ToList();
            var end = RoadEnd.Of(g.Edge(arms[i].Edge), arms[i].AtStart) with { Tag = tags[i], Links = new LinkSet(tags, mine) };
            g.SetEndData(arms[i].Edge, arms[i].AtStart, RoadEnd.Store(end));
        }
    }

    /// <summary>Points along a link's path with its heading, about a metre apart: a cubic curve leaving the lane coming
    /// in at <paramref name="p0"/> heading <paramref name="d0"/> and joining the lane going out at <paramref name="p3"/>
    /// heading <paramref name="d3"/>, square to the cuts. None for a path under half a metre.</summary>
    public static IEnumerable<(Vector2 P, Vector2 D)> Path(Vector2 p0, Vector2 d0, Vector2 p3, Vector2 d3, bool turn)
    {
        float chord = Vector2.Distance(p0, p3);
        if (chord < 0.5f) yield break;
        // 0.39 of the chord is a circle's arc for a right angle; a third for a straight run. A U-turn's ends are only
        // lanes apart, so it reaches into the junction a few metres first.
        float h = chord * (turn ? 0.39f : 1f / 3f);
        if (Vector2.Dot(d0, d3) < -0.7f) h = MathF.Max(h, 2.5f * chord);
        Vector2 p1 = p0 + d0 * h, p2 = p3 - d3 * h;
        int n = Math.Max(6, (int)MathF.Ceiling(chord + 2 * MathF.Max(0, h - chord)));
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n, u = 1 - t;
            var d = 3 * u * u * (p1 - p0) + 6 * u * t * (p2 - p1) + 3 * t * t * (p3 - p2);
            yield return (u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3,
                d.LengthSquared() < 1e-12f ? d0 : Vector2.Normalize(d));
        }
    }

    /// <summary>A link's path (<see cref="Path(Vector2, Vector2, Vector2, Vector2, bool)"/>).</summary>
    public static IEnumerable<(Vector2 P, Vector2 D)> Path(IReadOnlyList<ArmLanes> arms, LaneLink l)
    {
        var (a, b) = (arms[l.From], arms[l.To]);
        return Path(a.In[l.FromLane], -a.Outward, b.Out[l.ToLane], b.Outward, l.Move != Move.Straight);
    }
}
