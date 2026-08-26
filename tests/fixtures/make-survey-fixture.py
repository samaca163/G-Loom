"""Builds a Rhino document that exercises the survey classifier's whole rule surface.

Run inside Rhino (_RunPythonScript, or paste into _ScriptEditor) on an empty document.
Every layer below is a case the built-in schema is supposed to handle, plus two it is
supposed to refuse - a survey where nothing comes out unmapped is not a test.

Each layer gets one closed rectangle so Query Model Objects has something to return.
"""

import rhinoscriptsyntax as rs

# (layer, what the built-in schema should make of it)
CASES = [
    ("Muros",                          "wall / INTERIOR      es-muro"),
    ("Muro Exterior",                  "wall / EXTERIOR      es-muro-ext"),
    ("Losa de Entrepiso",              "floor                es-piso, not es-losa"),
    ("Cielo Raso",                     "ceiling              es-cielo"),
    ("Cubierta",                       "roof                 es-cubierta"),
    ("Puertas",                        "door                 es-puerta"),
    ("Ventanas",                       "window               es-ventana"),
    ("Ambientes",                      "room                 es-ambiente"),
    ("Nivel 1",                        "level                es-nivel"),
    ("Mobiliario",                     "furniture            es-mobiliario"),
    ("Exterior Walls",                 "wall / EXTERIOR      en-wall-ext"),
    ("Ground Floor Slab",              "floor                en-floor, not en-slab"),
    ("A-WALL-EXTR-E",                  "wall / EXTERIOR      ncs, phase EXISTING"),
    ("A-WALL-D",                       "wall / INTERIOR      ncs, phase DEMOLISH"),
    ("A-COLS",                         "column               ncs"),
    ("Arquitectura::Existente::Muros", "wall / INTERIOR      nested, classifies on its leaf"),
    ("Cotas",                          "UNMAPPED             expected - no rule covers it"),
    ("Ejes",                           "UNMAPPED             expected - no rule covers it"),
]

WIDTH, HEIGHT, GAP = 8.0, 5.0, 2.0


def build():
    rs.EnableRedraw(False)
    try:
        for index, (layer, _) in enumerate(CASES):
            rs.AddLayer(layer)
            rs.CurrentLayer(layer)

            origin = (0.0, index * (HEIGHT + GAP), 0.0)
            rs.AddRectangle(rs.MovePlane(rs.WorldXYPlane(), origin), WIDTH, HEIGHT)
    finally:
        rs.EnableRedraw(True)

    rs.ZoomExtents()

    print("[survey-fixture] {} layers, one rectangle each".format(len(CASES)))
    for layer, expected in CASES:
        print("[survey-fixture]   {:<32} {}".format(layer, expected))
    print("[survey-fixture] Expect 16 classified and 2 unmapped.")


if __name__ == "__main__":
    build()
