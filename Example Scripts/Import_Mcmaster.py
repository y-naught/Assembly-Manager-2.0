import rhinoscriptsyntax as rs
from System.IO import Path


def _get_unique_child_layer_name(base_name, parent_layer=None):
    """
    Returns a child layer name, without ::, that will be unique under the optional parent layer.
    Example: if base_name exists, returns base_name_01, base_name_02, etc.
    """
    child = base_name
    counter = 0
    while True:
        full = child if not parent_layer else (parent_layer + "::" + child)
        if not rs.IsLayer(full):
            return child
        counter += 1
        if counter < 10:
            child = "{}_{{:02d}}".format(base_name).format(counter)
        else:
            child = "{}_{}".format(base_name, counter)


def _get_unique_block_name(base_name):
    """
    Returns a block definition name that will be unique in the Rhino document.
    The first attempt is the STEP file base name. If that already exists,
    this returns base_name_01, base_name_02, etc.
    """
    block_name = base_name
    counter = 0
    while True:
        if not rs.IsBlock(block_name):
            return block_name
        counter += 1
        if counter < 10:
            block_name = "{}_{{:02d}}".format(base_name).format(counter)
        else:
            block_name = "{}_{}".format(base_name, counter)


def _ensure_parent_layer_exists(parent_layer):
    if parent_layer and not rs.IsLayer(parent_layer):
        rs.AddLayer(parent_layer)


def _safe_filename_base(filepath):
    """
    Gets the filename without extension and provides a fallback name if needed.
    """
    base_name = Path.GetFileNameWithoutExtension(filepath)
    if not base_name:
        base_name = "Imported_STEP"
    return base_name


def _create_block_from_imported_objects(imported_ids, base_name, layer_name, delete_input_geometry=True):
    """
    Creates a Rhino block definition from imported objects, using world origin as the block origin,
    then inserts one block instance at the world origin.

    Returns: (block_name, block_instance_id)
    """
    if not imported_ids:
        print("_create_block_from_imported_objects: no imported_ids provided")
        return None, None

    block_name = _get_unique_block_name(base_name)
    block_origin = (0.0, 0.0, 0.0)

    # Ensure definition geometry is assigned to the import layer before block creation.
    for oid in imported_ids:
        try:
            rs.ObjectLayer(oid, layer_name)
        except Exception as e:
            print("Warning: could not move object {} to layer {}: {}".format(oid, layer_name, e))

    # Create the block definition with its base point at world origin.
    # delete_input_geometry=True removes the loose imported geometry after it becomes block definition geometry,
    # leaving only the inserted block instance visible in model space.
    created_block_name = rs.AddBlock(imported_ids, block_origin, block_name, delete_input=delete_input_geometry)
    if not created_block_name:
        print("Warning: Could not create block definition. Imported objects remain as loose geometry.")
        return None, None

    # Insert one block instance at the world origin, with no rotation and unit scale.
    # NOTE: rhinoscriptsyntax.InsertBlock uses parameter name 'angle_degrees', not 'angle'.
    block_instance_id = rs.InsertBlock(created_block_name, block_origin, scale=(1.0, 1.0, 1.0), angle_degrees=0.0)
    if block_instance_id:
        try:
            rs.ObjectLayer(block_instance_id, layer_name)
        except Exception as e:
            print("Warning: could not move block instance {} to layer {}: {}".format(block_instance_id, layer_name, e))
    else:
        print("Warning: Block definition was created, but the block instance could not be inserted.")

    return created_block_name, block_instance_id


def import_step_create_layer_and_block(filepath=None, parent_layer=None, set_as_current=False, select_import=True, zoom_to_import=True):
    """
    Imports a STEP file, creates a layer named after the STEP file base name,
    creates a block definition named after the STEP file base name, and inserts
    one block instance at the world origin.

    - filepath: optional path to a .stp/.step file. If None, an OpenFile dialog is shown.
    - parent_layer: optional existing parent layer to nest the new layer under, e.g. "HARDWARE".
    - set_as_current: if True, the new layer remains current after import.
                      If False, the previous current layer is restored.
    - select_import: if True, selects the final block instance after import.
    - zoom_to_import: if True, zooms to the bounding box of the block instance.

    Returns:
        (new_layer_full_name, block_name, block_instance_id, imported_object_ids)
        or (None, None, None, []) on cancel/error.
    """
    # 1) Get filepath if needed.
    if not filepath:
        filter_str = "STEP Files (*.stp;*.step)|*.stp;*.step|All Files (*.*)|*.*||"
        filepath = rs.OpenFileName("Select STEP file to import", filter_str)
        if not filepath:
            print("Import cancelled: no file selected.")
            return None, None, None, []

    # Basic validation on extension.
    ext = (Path.GetExtension(filepath) or "").lower()
    if ext not in [".stp", ".step"]:
        print("Warning: The selected file does not have a .stp or .step extension.")

    # 2) Build layer and block names from the filename.
    base_name = _safe_filename_base(filepath)

    # Ensure parent exists if provided.
    _ensure_parent_layer_exists(parent_layer)

    # Make sure we have a unique child layer name under the parent.
    child_name = _get_unique_child_layer_name(base_name, parent_layer)

    # Create the layer, optionally nested under the parent.
    if parent_layer:
        new_layer_full = rs.AddLayer(child_name, parent=parent_layer)
        if not new_layer_full:
            new_layer_full = parent_layer + "::" + child_name
    else:
        new_layer_full = rs.AddLayer(child_name)
        if not new_layer_full:
            new_layer_full = child_name

    # 3) Capture current layer and objects before import.
    prev_current_layer = rs.CurrentLayer()

    # Temporarily make the new layer current for the import operation.
    rs.CurrentLayer(new_layer_full)

    pre_objs = rs.AllObjects(select=False)
    if pre_objs is None:
        pre_objs = []
    pre_set = set(pre_objs)

    # 4) Import the STEP file.
    rs.EnableRedraw(False)
    try:
        # Use command-line import and accept defaults with _Enter.
        safe_path = filepath.replace('\\', '/')
        cmd = "_-Import \"{}\" _Enter".format(safe_path)
        print("Running command:", cmd)
        rs.Command(cmd, echo=False)
    finally:
        rs.EnableRedraw(True)

    # 5) Determine which objects were added.
    post_objs = rs.AllObjects(select=False)
    if post_objs is None:
        post_objs = []
    imported_ids = list(set(post_objs) - pre_set)

    print("Imported object count:", len(imported_ids))

    # 6) Force all imported objects onto the new layer for certainty.
    for oid in imported_ids:
        try:
            rs.ObjectLayer(oid, new_layer_full)
        except Exception as e:
            print("Warning: could not move imported object {} to layer {}: {}".format(oid, new_layer_full, e))

    # 7) Create block definition from imported geometry and insert a block instance at world origin.
    block_name = None
    block_instance_id = None
    if imported_ids:
        block_name, block_instance_id = _create_block_from_imported_objects(
            imported_ids,
            base_name,
            new_layer_full,
            delete_input_geometry=True
        )

    # 8) Restore or keep current layer as requested.
    if set_as_current:
        rs.CurrentLayer(new_layer_full)
    else:
        rs.CurrentLayer(prev_current_layer)

    # 9) Post-selection and view.
    if select_import:
        rs.UnselectAllObjects()
        if block_instance_id:
            rs.SelectObject(block_instance_id)
            if zoom_to_import:
                bbox = rs.BoundingBox([block_instance_id])
                if bbox:
                    rs.ZoomBoundingBox(bbox)
        elif imported_ids:
            # Fallback if block creation failed.
            rs.SelectObjects(imported_ids)
            if zoom_to_import:
                bbox = rs.BoundingBox(imported_ids)
                if bbox:
                    rs.ZoomBoundingBox(bbox)

    if block_instance_id:
        print("Imported {} object(s) to layer: {}".format(len(imported_ids), new_layer_full))
        print("Created block definition: {}".format(block_name))
        print("Inserted block instance at world origin: 0, 0, 0")
    else:
        print("Imported {} object(s) to layer: {}".format(len(imported_ids), new_layer_full))
        print("No block instance was created.")

    return new_layer_full, block_name, block_instance_id, imported_ids


# Backward-compatible wrapper using the original function name.
def import_step_create_layer(filepath=None, parent_layer=None, set_as_current=False, select_import=True, zoom_to_import=True):
    return import_step_create_layer_and_block(
        filepath=filepath,
        parent_layer=parent_layer,
        set_as_current=set_as_current,
        select_import=select_import,
        zoom_to_import=zoom_to_import
    )


def main():
    # One-shot use: prompt for a STEP file, import it, create/move to a filename layer,
    # create a filename block definition, and insert the block at world origin.
    import_step_create_layer(
        filepath=None,
        parent_layer=None,
        set_as_current=False,
        select_import=True,
        zoom_to_import=True
    )


main()
