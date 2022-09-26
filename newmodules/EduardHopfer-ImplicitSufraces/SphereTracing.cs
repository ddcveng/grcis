using System;
using System.Collections.Generic;
using OpenTK;
using MathSupport;
using Utilities;
using Rendering;
using System.Linq;
using System.Net;

namespace EduardHopfer
{
  /// <summary>
  /// Ray-tracing rendering (w all secondary rays).
  /// Modified shade function that accounts for the quirks of sphere tracing
  /// which is used to render distance fields
  /// </summary>
  [Serializable]
  public sealed class SphereTracing : RayTracing
  {
    public SphereTracing ()
    {
      MaxLevel      = 12;
      MinImportance = 0.05;
      DoReflections =
      DoRefractions =
      DoShadows     =
      DoRecursion   = true;
    }

    /// <summary>
    /// Recursive shading function - computes color contribution of the given ray (shot from the
    /// origin 'p0' into direction vector 'p1''). Recursion is stopped
    /// by a hybrid method: 'importance' and 'level' are checked.
    /// Internal integration support.
    ///
    /// Distance fields specific:
    /// - blending of colors used with smooth minimum
    /// - offsetting of the intersection point to avoid self intersection
    /// </summary>
    /// <param name="depth">Current recursion depth.</param>
    /// <param name="importance">Importance of the current ray.</param>
    /// <param name="origin">Ray origin.</param>
    /// <param name="dir">Ray direction vector.</param>
    /// <param name="color">Result color.</param>
    /// <returns>Hash-value (ray sub-signature) used for adaptive subsampling.</returns>
    public override long shade (int depth,
                               double importance,
                               ref Vector3d origin,
                               ref Vector3d dir,
                               double[] color)
    {
      Vector3d direction = dir;

      int bands = color.Length;
      var intersections = MT.scene.Intersectable.Intersect(origin, dir);

      // If the ray is primary, increment both counters
      Statistics.IncrementRaysCounters(1, depth == 0);

      var i = Intersection.FirstIntersection(intersections, ref dir);

      if (i == null)
      {
        // No intersection -> background color
        rayRegisterer?.RegisterRay(AbstractRayRegisterer.RayType.rayVisualizerNormal, depth, origin, direction * 100000);

        return MT.scene.Background.GetColor(dir, color);
      }

      // There was at least one intersection
      i.Complete();
      // Complete distance field specific deferred data
      List<WeightedSurface> weights = null;
      if (i.SolidData is ImplicitData data)
      {
        weights = data.Weights.ToList();
      }

      rayRegisterer?.RegisterRay(AbstractRayRegisterer.RayType.unknown, depth, origin, i);

      // Hash code for adaptive supersampling
      long hash = i.Solid.GetHashCode();

      // Apply all the textures first
      if (i.Textures != null)
      {
        foreach (var tex in i.Textures)
        {
          hash = hash * HASH_TEXTURE + tex.Apply(i);
        }
      }

      // Point cloud shenanigans
      if (MT.pointCloudCheckBox && !MT.pointCloudSavingInProgress && !MT.singleRayTracing)
      {
        foreach (Intersection intersection in intersections)
        {
          if (!intersection.completed)
          {
            intersection.Complete();
          }

          if (intersection.Textures != null && !intersection.textureApplied)
          {
            foreach (var tex in intersection.Textures)
            {
              tex.Apply(intersection);
            }
          }

          double[] vertexColor = new double[3];
          Util.ColorCopy(intersection.SurfaceColor, vertexColor);
          Master.singleton?.pointCloud?.AddToPointCloud(intersection.CoordWorld, vertexColor, intersection.Normal, MT.threadID);
        }
      }

      // Color accumulation.
      Array.Clear(color, 0, bands);
      double[] comp = new double[bands];

      dir = -dir; // Reflect the viewing vector
      dir.Normalize();

      if (MT.scene.Sources == null ||
          MT.scene.Sources.Count < 1)
      {
        // No light sources at all.
        Util.ColorAdd(i.SurfaceColor, color);
      }
      else
      {
        // Apply the reflectance model for each source.
        i.Material = (IMaterial)i.Material.Clone();
        i.Material.Color = i.SurfaceColor;

        foreach (ILightSource source in MT.scene.Sources)
        {
          double[] intensity = source.GetIntensity(i, out Vector3d toLight);

          if (MT.singleRayTracing && source.position != null)
          {
            // Register shadow ray for RayVisualizer.
            rayRegisterer?.RegisterRay(AbstractRayRegisterer.RayType.rayVisualizerShadow, i.CoordWorld, (Vector3d)source.position);
          }

          if (intensity != null)
          {
            if (DoShadows && toLight != Vector3d.Zero)
            {
              toLight.Normalize(); // We need the direction vector to be normalized for proper sphere tracing
              var newOrigin = ImplicitCommon.GetNextRayOrigin(i, RayType.SHADOW);
              intersections = MT.scene.Intersectable.Intersect(newOrigin, toLight);

              Statistics.allRaysCount++;
              Intersection si = Intersection.FirstRealIntersection(intersections, ref toLight);
              // Better shadow testing: intersection between 0.0 and 1.0 kills the lighting.
              if (si != null && !si.Far(1.0, ref toLight))
              {
                continue;
              }
            }

            double[] fullReflection = new double[bands];
            bool doit = false;

            // Blend the materials according to the weights calculated in the intersection
            // Mainly used with smooth minimum based blending of shapes
            if (weights != null)
            {
              double fullIntensity = 0.0;

              foreach (var ws in weights)
              {
                // Reflectance Model.
                var reflectanceModel = (IReflectanceModel)ws.Solid.GetAttribute(PropertyName.REFLECTANCE_MODEL);
                if (reflectanceModel == null)
                  reflectanceModel = new PhongModel();

                // Material.
                var material = (IMaterial)ws.Solid.GetAttribute(PropertyName.MATERIAL);
                if (material == null)
                  material = reflectanceModel.DefaultMaterial();

                double[] col = (double[]) ws.Solid.GetAttribute(PropertyName.COLOR);
                if (col != null)
                {
                  material.Color = (double[]) col.Clone();
                }

                double[] reflection = reflectanceModel.ColorReflection(material, i.Normal, toLight, dir, ReflectionComponent.ALL);
                if (reflection == null)
                {
                  continue;
                }

                doit = true;
                fullIntensity += ws.Weight;
                Util.ColorAdd(reflection, ws.Weight, fullReflection);
              }

              Util.ColorMul(1.0 / fullIntensity, fullReflection);
            }
            else
            {
              fullReflection = i.ReflectanceModel.ColorReflection(i, toLight, dir, ReflectionComponent.ALL);
              doit = fullReflection != null;
            }

            if (doit)
            {
              Util.ColorAdd(fullReflection, intensity, color);
              hash = hash * HASH_LIGHT + source.GetHashCode();
            }
          }
        }
      }

      // Check the recursion depth.
      if (depth++ >= MaxLevel || (!DoReflections && !DoRefractions))
      {
        // No further recursion.
        return hash;
      }

      Vector3d r;
      double   maxK;
      double   newImportance;

      if (DoReflections)
      {
        // Shooting a reflected ray.
        Geometry.SpecularReflection(ref i.Normal, ref dir, out r);

        // TODO: Should interpolate between materials here also, but the effect is barely noticeable and there is no time
        double[] ks = i.ReflectanceModel.ColorReflection(i, dir, r, ReflectionComponent.SPECULAR_REFLECTION);
        if (ks != null)
        {
          maxK = ks[0];
          for (int b = 1; b < bands; b++)
            if (ks[b] > maxK)
              maxK = ks[b];

          newImportance = importance * maxK;
          if (newImportance >= MinImportance)
          {
            var newOrigin = ImplicitCommon.GetNextRayOrigin(i, RayType.REFLECT);

            // Do compute the reflected ray.
            hash += HASH_REFLECT * shade(depth, newImportance, ref newOrigin, ref r, comp);
            Util.ColorAdd(comp, ks, color);
          }
        }
      }

      if (DoRefractions)
      {
        // Shooting a refracted ray.
        maxK = i.Material.Kt;   // simple solution - no influence of reflectance model yet
        newImportance = importance * maxK;
        if (newImportance < MinImportance)
          return hash;

        // Refracted ray.
        if ((r = Geometry.SpecularRefraction(i.Normal, i.Material.n, dir)) == Vector3d.Zero)
          return hash;

        var newOrigin = ImplicitCommon.GetNextRayOrigin(i, RayType.REFRACT);
        hash += HASH_REFRACT * shade(depth, newImportance, ref newOrigin, ref r, comp);
        Util.ColorAdd(comp, maxK, color);
      }

      return hash;
    }
  }
}
