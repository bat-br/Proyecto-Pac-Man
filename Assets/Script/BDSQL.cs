using UnityEngine;
using System;
using System.IO;
using System.Text;
using TMPro;
using SQLite;

public class BDSQL : MonoBehaviour
{
    //Campos de UI
    [SerializeField] TMP_InputField inputNombre;
    [SerializeField] TMP_Text textoSalida;

    public bool registroBoleano = true;

    //Conexion con BD
    private SQLiteConnection bd; //Conexion abierta a base de datos
    private string rutaBD; //Ruta del archivo persistente BD

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private void Awake() //Establecer configuraciones de la escena
    {
        rutaBD = Path.Combine(Application.persistentDataPath, "ArcadeTerrorBD.db");

        //Abre o crea el archivo de la bd en la ruta
        bd = new SQLiteConnection(rutaBD);

        //Activa las llaves foraneas en SQLlite
        bd.Execute("PRAGMA foreign_keys = ON");

        Debug.Log("Base de datos en: " + rutaBD);

        //Crear las tablas
        CrearTablas();
    }

    private void CrearTablas()
    {
        //Tabla ESCUELA: Cada ESCUELA aparece solo una vez nombre UNICO
        bd.Execute(@"
                CREATE TABLE IF NOT EXISTS Puntuacion(
                    id      INTEGER PRIMARY KEY AUTOINCREMENT,
                    nombre  TEXT NOT NULL ,
                    puntos  INTEGER NOT NULL,
                    fecha   TEXT DEFAULT CURRENT_TIMESTAMP
                );");


    }

    public void GuardarNuevaPuntuacion(int puntosObtenidos)
    {
        if (!registroBoleano)
        {
            MostrarLeaderboard();
            return;
        }

        registroBoleano = false; // Bloquea registros duplicados en la misma partida

        string nombreJugador = inputNombre.text.Trim();
        if (string.IsNullOrEmpty(nombreJugador)) nombreJugador = "Anonimo";
        try
        {
            bd.Execute("INSERT INTO Puntuacion (nombre, puntos) VALUES (?, ?);", nombreJugador, puntosObtenidos);
            MostrarLeaderboard(); // Actualiza la tabla tras guardar
        }
        catch (Exception e)
        {
            Debug.Log("Error al guardar: " + e.Message);
        }
    }
    public void MostrarLeaderboard()
    {
        try
        {
            var top10 = bd.Query<FilaPuntuacion>("SELECT nombre, puntos FROM Puntuacion ORDER BY puntos DESC LIMIT 10;");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("TOP 10 SUPERVIVENTES");

            int posicion = 1;
            foreach (var fila in top10)
            {
                sb.AppendLine($"#{posicion} | {fila.nombre} : {fila.puntos} pts");
                posicion++;
            }
            textoSalida.text = sb.ToString();
        }
        catch (Exception e)
        {
            Debug.Log("Error al leer la base de datos: " + e.Message);
        }
    }
    [UnityEngine.Scripting.Preserve]
    public class FilaPuntuacion
    {
        [UnityEngine.Scripting.Preserve] public int id { get; set; }
        [UnityEngine.Scripting.Preserve] public string nombre { get; set; }
        [UnityEngine.Scripting.Preserve] public int puntos { get; set; }
        [UnityEngine.Scripting.Preserve] public string fecha { get; set; }
    }

    // Update is called once per frame
    void Update()
    {

    }
}







