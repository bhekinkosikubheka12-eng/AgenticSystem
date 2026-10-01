using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using AgenticSystem.Data;
using Firebase.Auth;

namespace AgenticSystem.Services;

public class FirebaseAuthService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public UserSession? CurrentUser { get; private set; }
    FirebaseAuthProvider authProvider = new FirebaseAuthProvider(new FirebaseConfig("AIzaSyDo5k36hK7SmkJnJCP1pm0c4syo4tg89wc"));
    public FirebaseAuthService(IConfiguration config, HttpClient httpClient)
    {
        _httpClient = httpClient;
        _apiKey = config["AiConfig:FirebaseApiKey"] ?? config["AiConfig:GeminiApiKey"] ?? "";
    }

    public async Task<AuthResult> RegisterAsync(string email, string password)
    {
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={_apiKey}";
        var payload = new { email, password, returnSecureToken = true };

        try
        {

            var reslt = await authProvider.CreateUserWithEmailAndPasswordAsync(email, password);

            var resultReturn = new AuthSuccessResponse()
            {
                 Email = email,
                  IdToken = reslt.FirebaseToken,
                   LocalId = reslt.User.LocalId
            };
            CurrentUser = new UserSession(resultReturn.LocalId, resultReturn.Email, resultReturn.IdToken);
            return new AuthResult(true, "Success");

            /*
            var response = await _httpClient.PostAsJsonAsync(url, payload);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadFromJsonAsync<AuthErrorResponse>();
                return new AuthResult(false, FormatErrorMessage(err?.Error?.Message ?? "Registration failed."));
            }

            var data = await response.Content.ReadFromJsonAsync<AuthSuccessResponse>();
            if (data != null)
            {
                CurrentUser = new UserSession(data.LocalId, data.Email, data.IdToken);
                return new AuthResult(true, "Success");
            }
            return new AuthResult(false, "Failed to read registration response."); */
        }
        catch (Exception ex)
        {
            return new AuthResult(false, ex.Message);
        }
    }

    public async Task<AuthResult> LoginAsync(string email, string password)
    {
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={_apiKey}";
        var payload = new { email, password, returnSecureToken = true };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(url, payload);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadFromJsonAsync<AuthErrorResponse>();
                return new AuthResult(false, FormatErrorMessage(err?.Error?.Message ?? "Invalid email or password."));
            }

            var data = await response.Content.ReadFromJsonAsync<AuthSuccessResponse>();
            if (data != null)
            {
                CurrentUser = new UserSession(data.LocalId, data.Email, data.IdToken);
                return new AuthResult(true, "Success");
            }
            return new AuthResult(false, "Failed to read login response.");
        }
        catch (Exception ex)
        {
            return new AuthResult(false, ex.Message);
        }
    }

    public void Logout()
    {
        CurrentUser = null;
    }

    private string FormatErrorMessage(string rawError)
    {
        return rawError switch
        {
            "EMAIL_EXISTS" => "The email address is already in use by another account.",
            "INVALID_EMAIL" => "The email address is badly formatted.",
            "MISSING_PASSWORD" => "Password cannot be empty.",
            "WEAK_PASSWORD" => "Password should be at least 6 characters.",
            "EMAIL_NOT_FOUND" => "There is no user record corresponding to this identifier.",
            "INVALID_PASSWORD" => "Incorrect password.",
            "USER_DISABLED" => "The user account has been disabled by an administrator.",
            _ => rawError
        };
    }
}
