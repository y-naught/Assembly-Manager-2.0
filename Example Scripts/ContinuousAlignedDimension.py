import Rhino
import scriptcontext as sc
from System import Guid
from System.Drawing import Color


# Page-layout-only continuous / aligned dimension string with live preview.
#
# IMPORTANT BEHAVIOR:
# - This version intentionally DOES NOT use an active detail viewport.
# - The layout page must be active, not the detail.
# - Picked points are interpreted as PAGE / PAPER coordinates.
# - Final dimensions are baked as PAGE SPACE annotation only.
# - No model-space dimensions are created.
#
# Workflow:
# 1. Go to a layout.
# 2. Make sure the detail is NOT active. If a detail is active, click/double-click outside it.
# 3. Run this script.
# 4. Pick page-space reference points over the layout/detail view.
# 5. Press Enter.
# 6. Pick the page-space dimension line location.


# -----------------------------
# Utility functions
# -----------------------------


def write_line(message):
    Rhino.RhinoApp.WriteLine(str(message))


def dot_vectors(a, b):
    return a.X * b.X + a.Y * b.Y + a.Z * b.Z


def vector_is_tiny(v):
    try:
        return v.IsTiny()
    except Exception:
        return v.Length < 1.0e-9


def safe_unitize(v):
    if vector_is_tiny(v):
        return False
    try:
        return v.Unitize()
    except Exception:
        return False


def project_point_to_plane(point, plane):
    normal = Rhino.Geometry.Vector3d(plane.Normal)
    if not safe_unitize(normal):
        return Rhino.Geometry.Point3d(point)

    vector = point - plane.Origin
    distance = dot_vectors(vector, normal)

    return Rhino.Geometry.Point3d(
        point.X - normal.X * distance,
        point.Y - normal.Y * distance,
        point.Z - normal.Z * distance
    )


def is_page_view(view):
    if view is None:
        return False

    try:
        if view.GetType().Name == "RhinoPageView":
            return True
    except Exception:
        pass

    try:
        if hasattr(view, "GetDetailViews"):
            return True
    except Exception:
        pass

    return False


def get_page_is_active(view):
    try:
        return bool(view.PageIsActive)
    except Exception:
        return False


def get_page_construction_plane(view):
    """
    Return the layout page viewport construction plane.
    When the page itself is active, this is the paper/layout coordinate system.
    """
    if view is None:
        return Rhino.Geometry.Plane.WorldXY

    viewport = None

    try:
        viewport = view.MainViewport
    except Exception:
        viewport = None

    if viewport is None:
        try:
            viewport = view.ActiveViewport
        except Exception:
            viewport = None

    if viewport is None:
        return Rhino.Geometry.Plane.WorldXY

    try:
        return viewport.ConstructionPlane()
    except Exception:
        try:
            return viewport.ConstructionPlane
        except Exception:
            return Rhino.Geometry.Plane.WorldXY


def get_current_dimension_style(doc):
    dim_styles = doc.DimStyles

    try:
        current_style = dim_styles.Current
        if current_style is not None and hasattr(current_style, "Id"):
            return current_style
    except Exception:
        pass

    try:
        current_style = dim_styles.CurrentDimensionStyle
        if current_style is not None and hasattr(current_style, "Id"):
            return current_style
    except Exception:
        pass

    try:
        index = dim_styles.CurrentDimensionStyleIndex
        current_style = dim_styles[index]
        if current_style is not None and hasattr(current_style, "Id"):
            return current_style
    except Exception:
        pass

    try:
        if dim_styles.Count > 0:
            return dim_styles[0]
    except Exception:
        pass

    return None


def get_layout_page_context(doc):
    """
    Validate that the active view is a layout page and that no detail viewport is active.

    This command is intentionally PAGE-SPACE ONLY. If a detail is active, Rhino's point
    getter returns model-space coordinates, which would be wrong for page-space dimensions.
    Therefore, the command stops and asks the user to deactivate the detail first.
    """
    view = doc.Views.ActiveView

    if view is None:
        write_line("No active view was found.")
        return None

    if not is_page_view(view):
        write_line("This version creates layout/page dimensions only. Switch to a layout page and run it again.")
        return None

    if not get_page_is_active(view):
        write_line("The detail viewport is currently active. Deactivate the detail first so the dimensions live only on the page layout, not in model space.")
        write_line("Click or double-click outside the detail viewport to activate the layout page, then run this command again.")
        return None

    context = {}
    context["view"] = view
    context["construction_plane"] = get_page_construction_plane(view)

    try:
        context["page_viewport_id"] = view.MainViewport.Id
    except Exception:
        context["page_viewport_id"] = Guid.Empty

    return context


def create_page_space_attributes(doc, page_context):
    """
    Create attributes that force final dimensions to exist on the layout page only.
    """
    attributes = Rhino.DocObjects.ObjectAttributes()
    attributes.LayerIndex = doc.Layers.CurrentLayerIndex

    try:
        attributes.Space = Rhino.DocObjects.ActiveSpace.PageSpace
    except Exception:
        pass

    try:
        viewport_id = page_context.get("page_viewport_id", Guid.Empty)
        if viewport_id != Guid.Empty:
            attributes.ViewportId = viewport_id
    except Exception:
        pass

    return attributes


# -----------------------------
# Dimension plane / coordinate logic
# -----------------------------


def build_dimension_plane(reference_points, dimension_line_point, construction_plane):
    """
    Builds a page-space dimension plane.

    X axis: first-to-last picked point projected onto the layout page CPlane.
    Y axis: perpendicular to X axis in the layout page CPlane.
    Origin: first projected page-space reference point.

    All distances are paper/layout distances because the detail viewport is inactive.
    """
    if reference_points is None or len(reference_points) < 2:
        raise Exception("At least two reference points are required.")

    projected_points = []
    for point in reference_points:
        projected_points.append(project_point_to_plane(point, construction_plane))

    projected_dim_line_point = project_point_to_plane(dimension_line_point, construction_plane)

    origin = projected_points[0]
    x_axis = projected_points[-1] - projected_points[0]

    if vector_is_tiny(x_axis):
        for i in range(len(projected_points) - 1):
            candidate = projected_points[i + 1] - projected_points[i]
            if not vector_is_tiny(candidate):
                x_axis = candidate
                break

    if not safe_unitize(x_axis):
        raise Exception("Could not determine a valid page-space dimension direction from the picked points.")

    page_normal = Rhino.Geometry.Vector3d(construction_plane.Normal)
    if not safe_unitize(page_normal):
        page_normal = Rhino.Geometry.Vector3d.ZAxis

    y_axis = Rhino.Geometry.Vector3d.CrossProduct(page_normal, x_axis)
    if not safe_unitize(y_axis):
        raise Exception("Could not determine a valid perpendicular direction for the page-space dimension line.")

    dimension_plane = Rhino.Geometry.Plane(origin, x_axis, y_axis)

    point_2d_list = []
    for point in projected_points:
        vector = point - origin
        x = dot_vectors(vector, x_axis)
        y = dot_vectors(vector, y_axis)
        point_2d_list.append(Rhino.Geometry.Point2d(x, y))

    dim_vector = projected_dim_line_point - origin
    dim_x = dot_vectors(dim_vector, x_axis)
    dim_y = dot_vectors(dim_vector, y_axis)
    dimension_line_2d = Rhino.Geometry.Point2d(dim_x, dim_y)

    return dimension_plane, point_2d_list, dimension_line_2d


def make_auto_dimension_line_point(reference_points, construction_plane, doc):
    """
    During point picking, the final dimension line is not chosen yet.
    This creates a temporary page-space offset for immediate preview.
    """
    if reference_points is None or len(reference_points) < 2:
        return None

    projected_points = []
    for point in reference_points:
        projected_points.append(project_point_to_plane(point, construction_plane))

    origin = projected_points[0]
    x_axis = projected_points[-1] - projected_points[0]

    if vector_is_tiny(x_axis):
        for i in range(len(projected_points) - 1):
            candidate = projected_points[i + 1] - projected_points[i]
            if not vector_is_tiny(candidate):
                x_axis = candidate
                break

    if not safe_unitize(x_axis):
        return None

    normal = Rhino.Geometry.Vector3d(construction_plane.Normal)
    if not safe_unitize(normal):
        normal = Rhino.Geometry.Vector3d.ZAxis

    y_axis = Rhino.Geometry.Vector3d.CrossProduct(normal, x_axis)
    if not safe_unitize(y_axis):
        return None

    x_values = []
    y_values = []
    for point in projected_points:
        vector = point - origin
        x_values.append(dot_vectors(vector, x_axis))
        y_values.append(dot_vectors(vector, y_axis))

    min_x = min(x_values)
    max_x = max(x_values)
    max_y = max(y_values)

    span_x = abs(max_x - min_x)
    if span_x < doc.ModelAbsoluteTolerance:
        span_x = 1.0

    offset = max(span_x * 0.16, doc.ModelAbsoluteTolerance * 40.0)
    target_y = max_y + offset
    mid_x = (min_x + max_x) * 0.5

    temp_plane = Rhino.Geometry.Plane(origin, x_axis, y_axis)
    return temp_plane.PointAt(mid_x, target_y)


def get_preview_size(point_2d_list, dimension_line_2d, doc):
    x_values = []
    y_values = []

    for p in point_2d_list:
        x_values.append(p.X)
        y_values.append(p.Y)

    x_values.append(dimension_line_2d.X)
    y_values.append(dimension_line_2d.Y)

    span_x = max(x_values) - min(x_values)
    span_y = max(y_values) - min(y_values)
    span = max(abs(span_x), abs(span_y))

    if span < doc.ModelAbsoluteTolerance:
        span = 1.0

    return max(span * 0.025, doc.ModelAbsoluteTolerance * 20.0)


def format_dimension_value(value):
    abs_value = abs(value)
    if abs_value >= 100.0:
        return "{0:.1f}".format(abs_value)
    if abs_value >= 10.0:
        return "{0:.2f}".format(abs_value)
    return "{0:.3f}".format(abs_value)


# -----------------------------
# Dynamic preview drawing
# -----------------------------


def draw_rubber_point_marker(display, point, color):
    try:
        display.DrawPoint(point, Rhino.Display.PointStyle.RoundSimple, 4, color)
    except Exception:
        try:
            display.DrawPoint(point, color)
        except Exception:
            pass


def draw_tick(display, plane, x, y, tick_size, sign, color, thickness):
    p1 = plane.PointAt(x - tick_size * 0.45, y - sign * tick_size * 0.45)
    p2 = plane.PointAt(x + tick_size * 0.45, y + sign * tick_size * 0.45)
    display.DrawLine(p1, p2, color, thickness)


def draw_preview_dimension_string(display, doc, reference_points, dimension_line_point, construction_plane, include_text):
    """
    Draw temporary page-space dimension graphics.
    Since the detail is inactive, these preview graphics are drawn over the layout page.
    """
    if reference_points is None or len(reference_points) < 2:
        return

    try:
        dimension_plane, point_2d_list, dimension_line_2d = build_dimension_plane(
            reference_points,
            dimension_line_point,
            construction_plane
        )
    except Exception:
        return

    if len(point_2d_list) < 2:
        return

    color_dim = Color.FromArgb(40, 150, 255)
    color_ext = Color.FromArgb(110, 190, 255)
    color_point = Color.FromArgb(255, 210, 60)
    color_text_fill = Color.FromArgb(245, 245, 245)
    color_text = Color.FromArgb(15, 15, 15)

    tick = get_preview_size(point_2d_list, dimension_line_2d, doc)

    average_ref_y = 0.0
    for p in point_2d_list:
        average_ref_y += p.Y
    average_ref_y = average_ref_y / float(len(point_2d_list))

    sign = 1.0
    if dimension_line_2d.Y < average_ref_y:
        sign = -1.0

    dim_y = dimension_line_2d.Y

    # Extension lines.
    for p in point_2d_list:
        ref_world = dimension_plane.PointAt(p.X, p.Y)
        dim_world = dimension_plane.PointAt(p.X, dim_y + sign * tick * 0.35)
        display.DrawLine(ref_world, dim_world, color_ext, 1)
        draw_rubber_point_marker(display, ref_world, color_point)

    # Dimension segments and labels.
    for i in range(len(point_2d_list) - 1):
        p1 = point_2d_list[i]
        p2 = point_2d_list[i + 1]

        line_start = dimension_plane.PointAt(p1.X, dim_y)
        line_end = dimension_plane.PointAt(p2.X, dim_y)
        display.DrawLine(line_start, line_end, color_dim, 2)

        if include_text:
            mid_x = (p1.X + p2.X) * 0.5
            text_y = dim_y + sign * tick * 1.75
            text_world = dimension_plane.PointAt(mid_x, text_y)
            value_text = format_dimension_value(p2.X - p1.X)

            try:
                display.DrawDot(text_world, value_text, color_text_fill, color_text)
            except Exception:
                pass

    # Ticks at all chain points.
    for p in point_2d_list:
        draw_tick(display, dimension_plane, p.X, dim_y, tick, sign, color_dim, 2)


def draw_reference_pick_preview(display, doc, picked_points, current_point, construction_plane):
    color_rubber = Color.FromArgb(150, 150, 150)
    color_point = Color.FromArgb(255, 210, 60)

    if picked_points is None or len(picked_points) == 0:
        return

    for p in picked_points:
        draw_rubber_point_marker(display, p, color_point)

    try:
        display.DrawLine(picked_points[-1], current_point, color_rubber, 1)
    except Exception:
        pass

    temp_points = []
    for p in picked_points:
        temp_points.append(p)

    if picked_points[-1].DistanceTo(current_point) > doc.ModelAbsoluteTolerance:
        temp_points.append(current_point)

    if len(temp_points) < 2:
        return

    auto_dim_line_point = make_auto_dimension_line_point(temp_points, construction_plane, doc)
    if auto_dim_line_point is None:
        return

    draw_preview_dimension_string(
        display,
        doc,
        temp_points,
        auto_dim_line_point,
        construction_plane,
        True
    )


# -----------------------------
# User interaction
# -----------------------------


def collect_reference_points_with_preview(doc, page_context):
    points = []
    construction_plane = page_context.get("construction_plane", Rhino.Geometry.Plane.WorldXY)

    while True:
        gp = Rhino.Input.Custom.GetPoint()

        if len(points) == 0:
            gp.SetCommandPrompt("Page continuous dimension: pick page/layout reference point 1")
        elif len(points) == 1:
            gp.SetCommandPrompt("Page continuous dimension: pick page/layout reference point 2")
        else:
            gp.SetCommandPrompt("Pick next page/layout reference point, or press Enter to finish")
            gp.AcceptNothing(True)

        if len(points) > 0:
            try:
                gp.SetBasePoint(points[-1], True)
            except Exception:
                pass

        def dynamic_draw(sender, e):
            try:
                draw_reference_pick_preview(
                    e.Display,
                    doc,
                    points,
                    e.CurrentPoint,
                    construction_plane
                )
            except Exception:
                pass

        gp.DynamicDraw += dynamic_draw
        result = gp.Get()

        try:
            gp.DynamicDraw -= dynamic_draw
        except Exception:
            pass

        if result == Rhino.Input.GetResult.Point:
            points.append(gp.Point())
            continue

        if result == Rhino.Input.GetResult.Nothing and len(points) >= 2:
            break

        if result == Rhino.Input.GetResult.Cancel:
            write_line("Page continuous dimension canceled.")
            return None

        if len(points) < 2:
            write_line("At least two points are required.")
            return None

        break

    return points


def collect_dimension_line_point_with_preview(doc, reference_points, page_context):
    construction_plane = page_context.get("construction_plane", Rhino.Geometry.Plane.WorldXY)

    gp = Rhino.Input.Custom.GetPoint()
    gp.SetCommandPrompt("Pick page/layout dimension line location")

    try:
        gp.SetBasePoint(reference_points[-1], True)
    except Exception:
        pass

    def dynamic_draw(sender, e):
        try:
            draw_preview_dimension_string(
                e.Display,
                doc,
                reference_points,
                e.CurrentPoint,
                construction_plane,
                True
            )
        except Exception:
            pass

    gp.DynamicDraw += dynamic_draw
    result = gp.Get()

    try:
        gp.DynamicDraw -= dynamic_draw
    except Exception:
        pass

    if result == Rhino.Input.GetResult.Point:
        return gp.Point()

    write_line("No dimension line location was picked. Command canceled.")
    return None


# -----------------------------
# Baking final PAGE-SPACE dimensions
# -----------------------------


def create_linear_dimension(doc, dim_style, dimension_plane, extension_point_1, extension_point_2, dimension_line_point, attributes):
    linear_dimension = None

    try:
        linear_dimension = Rhino.Geometry.LinearDimension.Create(
            dim_style,
            dimension_plane,
            extension_point_1,
            extension_point_2,
            dimension_line_point,
            1.0
        )
    except Exception:
        try:
            linear_dimension = Rhino.Geometry.LinearDimension(
                dimension_plane,
                extension_point_1,
                extension_point_2,
                dimension_line_point
            )
            try:
                if dim_style is not None:
                    linear_dimension.DimensionStyleId = dim_style.Id
            except Exception:
                pass
        except Exception as create_error:
            write_line("Failed to create a page-space linear dimension: {0}".format(create_error))
            return Guid.Empty

    if linear_dimension is None:
        write_line("Failed to create a page-space linear dimension object.")
        return Guid.Empty

    try:
        if dim_style is not None:
            linear_dimension.DimensionStyleId = dim_style.Id
    except Exception:
        pass

    try:
        object_id = doc.Objects.AddLinearDimension(linear_dimension, attributes)
    except Exception:
        try:
            object_id = doc.Objects.Add(linear_dimension, attributes)
        except Exception as add_error:
            write_line("Failed to add page-space dimension to document: {0}".format(add_error))
            return Guid.Empty

    if object_id == Guid.Empty:
        write_line("Rhino returned an empty object id while adding a page-space dimension.")

    return object_id


def group_dimension_objects(doc, object_ids):
    if object_ids is None or len(object_ids) == 0:
        return

    try:
        group_index = doc.Groups.Add("Page_Continuous_Aligned_Dimension")
        if group_index < 0:
            return

        for object_id in object_ids:
            try:
                doc.Groups.AddToGroup(group_index, object_id)
            except Exception:
                pass
    except Exception:
        pass


def warn_if_points_are_not_monotonic(point_2d_list):
    if point_2d_list is None or len(point_2d_list) < 3:
        return

    xs = []
    for p in point_2d_list:
        xs.append(p.X)

    increasing = True
    decreasing = True

    for i in range(len(xs) - 1):
        if xs[i + 1] < xs[i]:
            increasing = False
        if xs[i + 1] > xs[i]:
            decreasing = False

    if not increasing and not decreasing:
        write_line("Warning: picked points are not monotonic along the page dimension direction. Dimensions were still created in picked order.")


def create_page_continuous_aligned_dimensions(doc, reference_points, dimension_line_point, page_context):
    if reference_points is None or len(reference_points) < 2:
        write_line("At least two reference points are required.")
        return []

    construction_plane = page_context.get("construction_plane", Rhino.Geometry.Plane.WorldXY)
    dim_style = get_current_dimension_style(doc)
    attributes = create_page_space_attributes(doc, page_context)

    try:
        dimension_plane, point_2d_list, dimension_line_2d = build_dimension_plane(
            reference_points,
            dimension_line_point,
            construction_plane
        )
    except Exception as error:
        write_line("Could not build page-space dimension plane: {0}".format(error))
        return []

    warn_if_points_are_not_monotonic(point_2d_list)

    if abs(dimension_line_2d.Y) < doc.ModelAbsoluteTolerance:
        write_line("Warning: dimension line location is very close to the reference points; dimensions may overlap the picked layout geometry.")

    created_ids = []

    # Fallback behavior: one real LinearDimension per adjacent pair.
    # All are page-space dimensions on the same layout plane and same shared dimension-line offset.
    for i in range(len(point_2d_list) - 1):
        p1 = point_2d_list[i]
        p2 = point_2d_list[i + 1]
        segment_dim_line_point = Rhino.Geometry.Point2d((p1.X + p2.X) * 0.5, dimension_line_2d.Y)

        object_id = create_linear_dimension(
            doc,
            dim_style,
            dimension_plane,
            p1,
            p2,
            segment_dim_line_point,
            attributes
        )

        if object_id != Guid.Empty:
            created_ids.append(object_id)

    group_dimension_objects(doc, created_ids)
    return created_ids


# -----------------------------
# Main routine
# -----------------------------


def main():
    doc = sc.doc
    if doc is None:
        write_line("No active Rhino document was found.")
        return

    page_context = get_layout_page_context(doc)
    if page_context is None:
        return

    write_line("Creating PAGE-SPACE dimensions only. Detail viewport remains inactive; no model-space dimensions will be created.")

    reference_points = collect_reference_points_with_preview(doc, page_context)
    if reference_points is None or len(reference_points) < 2:
        return

    dimension_line_point = collect_dimension_line_point_with_preview(doc, reference_points, page_context)
    if dimension_line_point is None:
        return

    undo_record = None
    created_ids = []

    try:
        try:
            undo_record = doc.BeginUndoRecord("Page Continuous Aligned Dimension")
        except Exception:
            undo_record = None

        created_ids = create_page_continuous_aligned_dimensions(doc, reference_points, dimension_line_point, page_context)

    except Exception as error:
        write_line("Page continuous aligned dimension failed: {0}".format(error))

    finally:
        if undo_record is not None:
            try:
                doc.EndUndoRecord(undo_record)
            except Exception:
                pass

        try:
            doc.Views.Redraw()
        except Exception:
            pass

    if len(created_ids) > 0:
        write_line("Created {0} PAGE-SPACE aligned dimension segment(s).".format(len(created_ids)))
    else:
        write_line("No page-space dimensions were created.")


main()
