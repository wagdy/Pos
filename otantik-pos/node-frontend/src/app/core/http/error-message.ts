import { HttpErrorResponse } from '@angular/common/http';

export const tillServerUnreachable =
  'The till server cannot be reached. Check this device is on the restaurant network.';

// What to tell the cashier. The API writes every refusal as ProblemDetails with errors[0] meant
// to be shown as it is ("Quantity must be between 1 and 100.", "Look the customer up by mobile
// number before applying points."), the same shape the delivery system's API uses.
export function errorMessage(error: unknown, fallback = 'Something went wrong. Please try again.'): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) {
      return tillServerUnreachable;
    }
    const errors = error.error?.errors;
    if (Array.isArray(errors) && typeof errors[0] === 'string') {
      return errors[0];
    }
    // A model-validation 400: errors keyed by field.
    if (errors && typeof errors === 'object') {
      const first = Object.values(errors as Record<string, unknown>).flat()[0];
      if (typeof first === 'string') {
        return first;
      }
    }
    if (typeof error.error?.detail === 'string') {
      return error.error.detail;
    }
  }
  return fallback;
}
