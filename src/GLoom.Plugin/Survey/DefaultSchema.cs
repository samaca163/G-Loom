namespace GLoom.Survey;

/// <summary>
/// The schema the plugin falls back to when a project declares none, so the components
/// work in any Rhino with no setup. A project overrides it by committing its own file,
/// which is the point: changing "walls now need a fire rating" becomes a reviewable
/// commit with an author instead of a decision living in one person's head.
///
/// The rules are seeded from the NCS layer grammar and from plain-language layer names
/// in English and Spanish, so a survey drawn on a tablet - where nobody types
/// A-WALL-FULL-EXTR-E - still classifies.
/// </summary>
public static class DefaultSchema
{
    public const string Json = """
{
  "schemaVersion": 1,
  "id": "gloom-survey/1.0",
  "materialise": "full",
  "core": [
    {
      "id": "identity",
      "label": "Identity",
      "fields": [
        { "id": "category", "label": "Category", "type": "text", "required": true, "source": "rule" },
        { "id": "role", "label": "Role", "type": "text", "source": "rule" },
        { "id": "type", "label": "Type name", "type": "text", "source": "rule" },
        { "id": "mark", "label": "Mark", "type": "text", "revit": "ALL_MODEL_MARK" }
      ]
    },
    {
      "id": "location",
      "label": "Location",
      "fields": [
        { "id": "level", "label": "Level", "type": "text", "required": true },
        { "id": "offset", "label": "Offset from level", "type": "number", "unit": "mm", "default": "0" },
        { "id": "datum", "label": "Height datum", "type": "enum", "default": "LOCAL_FFL",
          "values": ["PROJECT_BASE", "SURVEY_POINT", "LOCAL_FFL", "CUSTOM"] },
        { "id": "face", "label": "Measured face", "type": "enum", "default": "NOT_STATED",
          "values": ["CENTRELINE", "FINISH_INT", "FINISH_EXT", "CORE_INT", "CORE_EXT", "STRUCTURAL", "NOT_STATED"] },
        { "id": "zone", "label": "Zone", "type": "text" }
      ]
    },
    {
      "id": "phase",
      "label": "Phase",
      "fields": [
        { "id": "status", "label": "Phase status", "type": "enum", "required": true, "source": "rule", "default": "EXISTING",
          "values": ["EXISTING", "DEMOLISH", "NEW", "TEMPORARY", "OTHER", "NOTKNOWN"] },
        { "id": "built", "label": "Year built", "type": "text" }
      ]
    },
    {
      "id": "class",
      "label": "Classification",
      "fields": [
        { "id": "uniformat", "label": "Uniformat code", "type": "text", "revit": "UNIFORMAT_CODE" },
        { "id": "omniclass", "label": "OmniClass number", "type": "text", "revit": "OMNICLASS_CODE" },
        { "id": "keynote", "label": "Keynote", "type": "text", "revit": "KEYNOTE_PARAM" }
      ]
    },
    {
      "id": "accuracy",
      "label": "Accuracy",
      "fields": [
        { "id": "basis", "label": "Basis", "type": "enum", "required": true, "default": "MEASURED",
          "values": ["MEASURED", "INFERRED", "FROM_DRAWING", "REPORTED", "ASSUMED"] },
        { "id": "loa", "label": "USIBD level of accuracy", "type": "text" },
        { "id": "tolerance", "label": "Tolerance", "type": "number", "unit": "mm" },
        { "id": "obscured", "label": "Obscured", "type": "bool", "default": "false" }
      ]
    },
    {
      "id": "source",
      "label": "Provenance",
      "fields": [
        { "id": "method", "label": "Measurement method", "type": "enum", "required": true, "default": "LASER_DISTO",
          "values": ["LASER_DISTO", "TAPE", "TOTAL_STATION", "TLS_SCAN", "PHOTOGRAMMETRY", "DRAWING_DERIVED", "ESTIMATED", "NOT_MEASURED"] },
        { "id": "by", "label": "Surveyed by", "type": "text", "required": true },
        { "id": "date", "label": "Survey date", "type": "date", "required": true },
        { "id": "verified", "label": "Verification", "type": "enum", "required": true, "default": "UNVERIFIED",
          "values": ["UNVERIFIED", "PRESUMED", "FIELD_VERIFIED", "SECOND_CHECK", "CLIENT_APPROVED", "SUPERSEDED"] },
        { "id": "photo", "label": "Photo reference", "type": "text" },
        { "id": "notes", "label": "Notes", "type": "text", "revit": "ALL_MODEL_INSTANCE_COMMENTS" }
      ]
    },
    {
      "id": "condition",
      "label": "Condition",
      "fields": [
        { "id": "grade", "label": "Condition grade", "type": "integer", "values": ["1", "2", "3", "4", "5"] },
        { "id": "defect", "label": "Defect class", "type": "enum", "default": "NONE",
          "values": ["NONE", "STRUCTURAL_MOVEMENT", "WATER_INGRESS", "FUNGAL_ATTACK", "INSECT_ATTACK", "CORROSION", "SURFACE_DETERIORATION"] },
        { "id": "priority", "label": "Repair priority", "type": "integer", "values": ["1", "2", "3", "4"] },
        { "id": "specialist", "label": "Specialist needed", "type": "bool", "default": "false" }
      ]
    },
    {
      "id": "hazard",
      "label": "Hazards",
      "fields": [
        { "id": "present", "label": "Hazard present", "type": "enum", "required": true, "default": "NO",
          "values": ["NO", "YES", "PRESUMED", "NOT_ACCESSED"] },
        { "id": "type", "label": "Hazard type", "type": "enum",
          "values": ["ASBESTOS", "LEAD", "SILICA_DUST", "WOOD_DUST", "CHEMICAL", "OTHER"] },
        { "id": "action", "label": "Action", "type": "enum",
          "values": ["NONE", "MONITOR", "PROTECT", "SEAL", "REPAIR", "REMOVE"] }
      ]
    },
    {
      "id": "asset",
      "label": "Asset",
      "fields": [
        { "id": "tag", "label": "Asset tag", "type": "text" },
        { "id": "serial", "label": "Serial number", "type": "text" },
        { "id": "maker", "label": "Manufacturer", "type": "text" },
        { "id": "model", "label": "Model", "type": "text" }
      ]
    }
  ],
  "categories": [
    {
      "id": "wall", "label": "Wall", "revit": "Walls", "uniformat": "B2010",
      "fields": [
        { "id": "thickness", "label": "Thickness", "type": "number", "unit": "mm", "required": true },
        { "id": "height", "label": "Height", "type": "number", "unit": "mm" },
        { "id": "topLevel", "label": "Top level", "type": "text" },
        { "id": "bearing", "label": "Load bearing", "type": "enum", "default": "UNKNOWN",
          "values": ["CONFIRMED_OPENED_UP", "CONFIRMED_FROM_DRAWING", "ASSUMED_FROM_THICKNESS", "ASSUMED_FROM_ALIGNMENT", "NOT_BEARING", "UNKNOWN"] },
        { "id": "buildup", "label": "Build-up", "type": "text" },
        { "id": "fire", "label": "Fire rating", "type": "text" }
      ]
    },
    {
      "id": "column", "label": "Column", "revit": "Columns", "uniformat": "B1010",
      "fields": [
        { "id": "shape", "label": "Shape", "type": "enum", "required": true, "default": "RECTANGULAR",
          "values": ["RECTANGULAR", "CIRCULAR", "L_SHAPE", "T_SHAPE", "STEEL_SECTION", "IRREGULAR"] },
        { "id": "width", "label": "Width", "type": "number", "unit": "mm", "required": true },
        { "id": "depth", "label": "Depth", "type": "number", "unit": "mm" },
        { "id": "baseLevel", "label": "Base level", "type": "text" },
        { "id": "topLevel", "label": "Top level", "type": "text" },
        { "id": "material", "label": "Material", "type": "text" }
      ]
    },
    {
      "id": "floor", "label": "Floor", "revit": "Floors", "uniformat": "B1010",
      "fields": [
        { "id": "thickness", "label": "Thickness", "type": "number", "unit": "mm", "required": true },
        { "id": "offset", "label": "Height offset", "type": "number", "unit": "mm", "default": "0" },
        { "id": "structure", "label": "Structure", "type": "text" },
        { "id": "finish", "label": "Floor finish", "type": "text" }
      ]
    },
    {
      "id": "ceiling", "label": "Ceiling", "revit": "Ceilings", "uniformat": "C3030",
      "fields": [
        { "id": "form", "label": "Form", "type": "enum", "required": true, "default": "PLASTERBOARD",
          "values": ["PLASTERBOARD", "SUSPENDED_GRID", "EXPOSED_SOFFIT", "TIMBER", "VAULTED", "OTHER"] },
        { "id": "height", "label": "Height above floor", "type": "number", "unit": "mm", "required": true },
        { "id": "void", "label": "Void depth", "type": "number", "unit": "mm" },
        { "id": "finish", "label": "Ceiling finish", "type": "text" }
      ]
    },
    {
      "id": "roof", "label": "Roof", "revit": "Roofs", "uniformat": "B3010",
      "fields": [
        { "id": "form", "label": "Form", "type": "enum", "required": true, "default": "FLAT",
          "values": ["FLAT", "PITCHED", "MONOPITCH", "HIPPED", "MANSARD", "VAULTED", "OTHER"] },
        { "id": "slope", "label": "Slope", "type": "number", "unit": "deg" },
        { "id": "covering", "label": "Covering", "type": "text" },
        { "id": "offset", "label": "Base offset", "type": "number", "unit": "mm", "default": "0" }
      ]
    },
    {
      "id": "door", "label": "Door", "revit": "Doors", "uniformat": "C1020",
      "fields": [
        { "id": "width", "label": "Structural width", "type": "number", "unit": "mm", "required": true },
        { "id": "height", "label": "Structural height", "type": "number", "unit": "mm", "required": true },
        { "id": "hand", "label": "Hand", "type": "enum", "default": "UNKNOWN", "values": ["LEFT", "RIGHT", "UNKNOWN"] },
        { "id": "swing", "label": "Swing", "type": "enum", "default": "UNKNOWN", "values": ["IN", "OUT", "BOTH", "SLIDING", "FOLDING", "FIXED", "UNKNOWN"] },
        { "id": "clear", "label": "Clear opening width", "type": "number", "unit": "mm" },
        { "id": "fire", "label": "Fire rating", "type": "text" }
      ]
    },
    {
      "id": "window", "label": "Window", "revit": "Windows", "uniformat": "B2020",
      "fields": [
        { "id": "width", "label": "Structural width", "type": "number", "unit": "mm", "required": true },
        { "id": "height", "label": "Structural height", "type": "number", "unit": "mm", "required": true },
        { "id": "sill", "label": "Sill height above floor", "type": "number", "unit": "mm", "required": true },
        { "id": "operation", "label": "Operation", "type": "enum", "default": "UNKNOWN",
          "values": ["FIXED", "CASEMENT", "SASH", "TILT_TURN", "SLIDING", "PIVOT", "LOUVRE", "UNKNOWN"] },
        { "id": "glazing", "label": "Glazing", "type": "enum", "default": "UNKNOWN",
          "values": ["SINGLE", "DOUBLE", "TRIPLE", "SECONDARY", "OBSCURED", "NONE_OPEN", "UNKNOWN"] },
        { "id": "frame", "label": "Frame material", "type": "text" }
      ]
    },
    {
      "id": "room", "label": "Room", "revit": "Rooms",
      "fields": [
        { "id": "number", "label": "Number", "type": "text", "required": true, "revit": "ROOM_NUMBER" },
        { "id": "name", "label": "Name", "type": "text", "required": true, "revit": "ROOM_NAME" },
        { "id": "ceiling", "label": "Ceiling height", "type": "number", "unit": "mm", "required": true },
        { "id": "limit", "label": "Limit offset", "type": "number", "unit": "mm", "revit": "ROOM_UPPER_OFFSET" },
        { "id": "use", "label": "Use", "type": "text" },
        { "id": "standard", "label": "Area standard", "type": "text" }
      ]
    },
    {
      "id": "level", "label": "Level", "revit": "Levels",
      "fields": [
        { "id": "name", "label": "Name", "type": "text", "required": true },
        { "id": "elevation", "label": "Elevation", "type": "number", "unit": "mm", "required": true },
        { "id": "surface", "label": "Reference surface", "type": "enum", "default": "FFL",
          "values": ["FFL", "SSL", "SOFFIT", "OTHER"] }
      ]
    },
    {
      "id": "furniture", "label": "Furniture", "revit": "Furniture", "uniformat": "E2010",
      "fields": [
        { "id": "rotation", "label": "Rotation", "type": "number", "unit": "deg", "default": "0" },
        { "id": "width", "label": "Width", "type": "number", "unit": "mm" },
        { "id": "depth", "label": "Depth", "type": "number", "unit": "mm" },
        { "id": "height", "label": "Height", "type": "number", "unit": "mm" },
        { "id": "fixed", "label": "Built in", "type": "bool", "default": "false" }
      ]
    },
    {
      "id": "generic", "label": "Generic", "revit": "Generic Models",
      "fields": [
        { "id": "name", "label": "Name", "type": "text", "required": true },
        { "id": "intended", "label": "Intended category", "type": "text" },
        { "id": "reason", "label": "Why generic", "type": "text" }
      ]
    }
  ],
  "rules": [
    { "id": "ncs-wall-ext", "kind": "ncs", "pattern": "A-WALL+EXTR", "category": "wall", "role": "EXTERIOR" },
    { "id": "ncs-wall", "kind": "ncs", "pattern": "A-WALL", "category": "wall", "role": "INTERIOR" },
    { "id": "ncs-column", "kind": "ncs", "pattern": "A-COLS", "category": "column" },
    { "id": "ncs-floor", "kind": "ncs", "pattern": "A-FLOR", "category": "floor" },
    { "id": "ncs-ceiling", "kind": "ncs", "pattern": "A-CLNG", "category": "ceiling" },
    { "id": "ncs-roof", "kind": "ncs", "pattern": "A-ROOF", "category": "roof" },
    { "id": "ncs-door", "kind": "ncs", "pattern": "A-DOOR", "category": "door" },
    { "id": "ncs-window", "kind": "ncs", "pattern": "A-GLAZ", "category": "window" },
    { "id": "ncs-room", "kind": "ncs", "pattern": "A-AREA", "category": "room" },
    { "id": "ncs-furniture", "kind": "ncs", "pattern": "A-FURN", "category": "furniture" },

    { "id": "en-wall-ext", "kind": "glob", "pattern": "*EXTERIOR*WALL*", "category": "wall", "role": "EXTERIOR" },
    { "id": "en-wall", "kind": "glob", "pattern": "*WALL*", "category": "wall", "role": "INTERIOR" },
    { "id": "en-column", "kind": "glob", "pattern": "*COLUMN*", "category": "column" },
    { "id": "en-floor", "kind": "glob", "pattern": "*FLOOR*", "category": "floor" },
    { "id": "en-slab", "kind": "glob", "pattern": "*SLAB*", "category": "floor" },
    { "id": "en-ceiling", "kind": "glob", "pattern": "*CEILING*", "category": "ceiling" },
    { "id": "en-roof", "kind": "glob", "pattern": "*ROOF*", "category": "roof" },
    { "id": "en-door", "kind": "glob", "pattern": "*DOOR*", "category": "door" },
    { "id": "en-window", "kind": "glob", "pattern": "*WINDOW*", "category": "window" },
    { "id": "en-room", "kind": "glob", "pattern": "*ROOM*", "category": "room" },
    { "id": "en-level", "kind": "glob", "pattern": "*LEVEL*", "category": "level" },
    { "id": "en-furniture", "kind": "glob", "pattern": "*FURNITURE*", "category": "furniture" },

    { "id": "es-muro-ext", "kind": "glob", "pattern": "*MURO*EXTERIOR*", "category": "wall", "role": "EXTERIOR" },
    { "id": "es-muro", "kind": "glob", "pattern": "*MURO*", "category": "wall", "role": "INTERIOR" },
    { "id": "es-columna", "kind": "glob", "pattern": "*COLUMNA*", "category": "column" },
    { "id": "es-piso", "kind": "glob", "pattern": "*PISO*", "category": "floor" },
    { "id": "es-losa", "kind": "glob", "pattern": "*LOSA*", "category": "floor" },
    { "id": "es-cielo", "kind": "glob", "pattern": "*CIELO*", "category": "ceiling" },
    { "id": "es-techo", "kind": "glob", "pattern": "*TECHO*", "category": "ceiling" },
    { "id": "es-cubierta", "kind": "glob", "pattern": "*CUBIERTA*", "category": "roof" },
    { "id": "es-puerta", "kind": "glob", "pattern": "*PUERTA*", "category": "door" },
    { "id": "es-ventana", "kind": "glob", "pattern": "*VENTANA*", "category": "window" },
    { "id": "es-espacio", "kind": "glob", "pattern": "*ESPACIO*", "category": "room" },
    { "id": "es-ambiente", "kind": "glob", "pattern": "*AMBIENTE*", "category": "room" },
    { "id": "es-nivel", "kind": "glob", "pattern": "*NIVEL*", "category": "level" },
    { "id": "es-mobiliario", "kind": "glob", "pattern": "*MOBILIARIO*", "category": "furniture" },
    { "id": "es-mueble", "kind": "glob", "pattern": "*MUEBLE*", "category": "furniture" }
  ]
}
""";
}
