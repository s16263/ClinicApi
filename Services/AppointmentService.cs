using System.Data;
using ClinicApi.DTOs;
using Microsoft.Data.SqlClient;

public class AppointmentService
{
    private readonly string _connectionString;
    private static readonly string[] AllowedStatuses = ["Scheduled", "Completed", "Cancelled"];

    public AppointmentService(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")!;
    }

    public async Task<List<AppointmentListDto>> GetAppointmentsAsync(string? status, string? patientLastName)
    {
        var result = new List<AppointmentListDto>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("""
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason,
                   p.FirstName + N' ' + p.LastName AS PatientFullName,
                   p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
            ORDER BY a.AppointmentDate;
            """, connection);

        command.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = (object?)status ?? DBNull.Value;
        command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar, 80).Value = (object?)patientLastName ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status = reader.GetString(2),
                Reason = reader.GetString(3),
                PatientFullName = reader.GetString(4),
                PatientEmail = reader.GetString(5)
            });
        }

        return result;
    }

    public async Task<AppointmentDetailsDto?> GetAppointmentByIdAsync(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand("""
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes, a.CreatedAt,
                   p.IdPatient, p.FirstName + N' ' + p.LastName AS PatientFullName, p.Email, p.PhoneNumber,
                   d.IdDoctor, d.FirstName + N' ' + d.LastName AS DoctorFullName, d.LicenseNumber,
                   s.Name AS SpecializationName
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
            JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
            JOIN dbo.Specializations s ON s.IdSpecialization = d.IdSpecialization
            WHERE a.IdAppointment = @IdAppointment;
            """, connection);

        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32(0),
            AppointmentDate = reader.GetDateTime(1),
            Status = reader.GetString(2),
            Reason = reader.GetString(3),
            InternalNotes = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetDateTime(5),
            IdPatient = reader.GetInt32(6),
            PatientFullName = reader.GetString(7),
            PatientEmail = reader.GetString(8),
            PatientPhoneNumber = reader.GetString(9),
            IdDoctor = reader.GetInt32(10),
            DoctorFullName = reader.GetString(11),
            DoctorLicenseNumber = reader.GetString(12),
            SpecializationName = reader.GetString(13)
        };
    }

    public async Task<(bool Success, int? Id, string? Error, int StatusCode)> CreateAppointmentAsync(CreateAppointmentRequestDto dto)
    {
        var validation = ValidateCreate(dto);
        if (validation != null) return (false, null, validation, 400);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        if (!await ActiveExistsAsync(connection, "Patients", "IdPatient", dto.IdPatient)) return (false, null, "Pacjent nie istnieje albo jest nieaktywny.", 400);
        if (!await ActiveExistsAsync(connection, "Doctors", "IdDoctor", dto.IdDoctor)) return (false, null, "Lekarz nie istnieje albo jest nieaktywny.", 400);
        if (await DoctorHasConflictAsync(connection, dto.IdDoctor, dto.AppointmentDate, null)) return (false, null, "Lekarz ma już wizytę w tym terminie.", 409);

        await using var command = new SqlCommand("""
            INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason, InternalNotes)
            OUTPUT INSERTED.IdAppointment
            VALUES (@IdPatient, @IdDoctor, @AppointmentDate, N'Scheduled', @Reason, NULL);
            """, connection);

        command.Parameters.Add("@IdPatient", SqlDbType.Int).Value = dto.IdPatient;
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = dto.AppointmentDate;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = dto.Reason.Trim();

        var id = (int)await command.ExecuteScalarAsync();
        return (true, id, null, 201);
    }

    public async Task<(bool Success, string? Error, int StatusCode)> UpdateAppointmentAsync(int idAppointment, UpdateAppointmentRequestDto dto)
    {
        var validation = ValidateUpdate(dto);
        if (validation != null) return (false, validation, 400);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var current = await GetCurrentAppointmentAsync(connection, idAppointment);
        if (current == null) return (false, "Wizyta nie istnieje.", 404);

        if (!await ActiveExistsAsync(connection, "Patients", "IdPatient", dto.IdPatient)) return (false, "Pacjent nie istnieje albo jest nieaktywny.", 400);
        if (!await ActiveExistsAsync(connection, "Doctors", "IdDoctor", dto.IdDoctor)) return (false, "Lekarz nie istnieje albo jest nieaktywny.", 400);
        if (current.Value.Status == "Completed" && current.Value.AppointmentDate != dto.AppointmentDate) return (false, "Nie można zmienić terminu zakończonej wizyty.", 409);
        if (await DoctorHasConflictAsync(connection, dto.IdDoctor, dto.AppointmentDate, idAppointment)) return (false, "Lekarz ma już inną wizytę w tym terminie.", 409);

        await using var command = new SqlCommand("""
            UPDATE dbo.Appointments
            SET IdPatient = @IdPatient,
                IdDoctor = @IdDoctor,
                AppointmentDate = @AppointmentDate,
                Status = @Status,
                Reason = @Reason,
                InternalNotes = @InternalNotes
            WHERE IdAppointment = @IdAppointment;
            """, connection);

        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        command.Parameters.Add("@IdPatient", SqlDbType.Int).Value = dto.IdPatient;
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = dto.IdDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = dto.AppointmentDate;
        command.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = dto.Status.Trim();
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = dto.Reason.Trim();
        command.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500).Value = (object?)dto.InternalNotes ?? DBNull.Value;

        await command.ExecuteNonQueryAsync();
        return (true, null, 200);
    }

    public async Task<(bool Success, string? Error, int StatusCode)> DeleteAppointmentAsync(int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var current = await GetCurrentAppointmentAsync(connection, idAppointment);
        if (current == null) return (false, "Wizyta nie istnieje.", 404);
        if (current.Value.Status == "Completed") return (false, "Nie można usunąć zakończonej wizyty.", 409);

        await using var command = new SqlCommand("DELETE FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;", connection);
        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        await command.ExecuteNonQueryAsync();

        return (true, null, 204);
    }

    private static string? ValidateCreate(CreateAppointmentRequestDto dto)
    {
        if (dto.IdPatient <= 0) return "IdPatient musi być większe od 0.";
        if (dto.IdDoctor <= 0) return "IdDoctor musi być większe od 0.";
        if (dto.AppointmentDate <= DateTime.Now) return "Termin wizyty nie może być w przeszłości.";
        if (string.IsNullOrWhiteSpace(dto.Reason)) return "Opis wizyty nie może być pusty.";
        if (dto.Reason.Length > 250) return "Opis wizyty może mieć maksymalnie 250 znaków.";
        return null;
    }

    private static string? ValidateUpdate(UpdateAppointmentRequestDto dto)
    {
        var createError = ValidateCreate(new CreateAppointmentRequestDto
        {
            IdPatient = dto.IdPatient,
            IdDoctor = dto.IdDoctor,
            AppointmentDate = dto.AppointmentDate,
            Reason = dto.Reason
        });
        if (createError != null) return createError;
        if (!AllowedStatuses.Contains(dto.Status)) return "Status musi być jednym z: Scheduled, Completed, Cancelled.";
        if (dto.InternalNotes?.Length > 500) return "Notatki mogą mieć maksymalnie 500 znaków.";
        return null;
    }

    private static async Task<bool> ActiveExistsAsync(SqlConnection connection, string tableName, string idColumn, int id)
    {
        await using var command = new SqlCommand($"SELECT COUNT(1) FROM dbo.{tableName} WHERE {idColumn} = @Id AND IsActive = 1;", connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        return (int)await command.ExecuteScalarAsync() > 0;
    }

    private static async Task<bool> DoctorHasConflictAsync(SqlConnection connection, int idDoctor, DateTime date, int? ignoredAppointmentId)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(1)
            FROM dbo.Appointments
            WHERE IdDoctor = @IdDoctor
              AND AppointmentDate = @AppointmentDate
              AND Status = N'Scheduled'
              AND (@IgnoredAppointmentId IS NULL OR IdAppointment <> @IgnoredAppointmentId);
            """, connection);
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = date;
        command.Parameters.Add("@IgnoredAppointmentId", SqlDbType.Int).Value = (object?)ignoredAppointmentId ?? DBNull.Value;
        return (int)await command.ExecuteScalarAsync() > 0;
    }

    private static async Task<(string Status, DateTime AppointmentDate)?> GetCurrentAppointmentAsync(SqlConnection connection, int idAppointment)
    {
        await using var command = new SqlCommand("SELECT Status, AppointmentDate FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;", connection);
        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetString(0), reader.GetDateTime(1));
    }
}
