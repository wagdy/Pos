import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { MAT_FORM_FIELD_DEFAULT_OPTIONS } from '@angular/material/form-field';
import { provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { AuthService } from './core/auth/auth.service';
import { authInterceptor, reachabilityInterceptor } from './core/http/interceptors';
import { WesternDigits } from './core/ui/western-digits';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([reachabilityInterceptor, authInterceptor])),
    // Before the first route resolves: who is signed in, as GET /api/auth/me says.
    provideAppInitializer(() => inject(AuthService).restore()),
    // Arabic digits typed into any field that takes numbers go in as Western ones.
    provideAppInitializer(() => inject(WesternDigits).install()),
    { provide: MAT_FORM_FIELD_DEFAULT_OPTIONS, useValue: { appearance: 'outline' } },
  ],
};
